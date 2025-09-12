using Azure.Communication.CallAutomation;
using Microsoft.AspNetCore.Http;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Text;
using System.Buffers;

namespace ACSforMCS.Middleware
{
    /// <summary>
    /// WebSocket middleware that handles real-time audio streaming and transcription for Azure Communication Services calls.
    /// This middleware establishes WebSocket connections to receive streaming audio data from ACS,
    /// processes transcription results, and forwards them to the bot conversation.
    /// </summary>
    public class WebSocketMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<WebSocketMiddleware> _logger;
        private readonly CallAutomationClient _client;
        private readonly ConcurrentDictionary<string, CallContext> _callStore;
        private readonly Services.CallAutomationService _callAutomationService;

        /// <summary>
        /// Initializes a new instance of the WebSocketMiddleware with required dependencies.
        /// </summary>
        /// <param name="next">The next middleware in the pipeline</param>
        /// <param name="logger">Logger for tracking WebSocket operations</param>
        /// <param name="client">Azure Communication Services CallAutomation client</param>
        /// <param name="callStore">Thread-safe store for managing active call contexts</param>
        /// <param name="callAutomationService">Service for handling call automation operations</param>
        public WebSocketMiddleware(
            RequestDelegate next,
            ILogger<WebSocketMiddleware> logger,
            CallAutomationClient client,
            ConcurrentDictionary<string, CallContext> callStore,
            Services.CallAutomationService callAutomationService)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _callStore = callStore ?? throw new ArgumentNullException(nameof(callStore));
            _callAutomationService = callAutomationService ?? throw new ArgumentNullException(nameof(callAutomationService));
        }

        /// <summary>
        /// Processes incoming HTTP requests and handles WebSocket upgrade requests for call streaming.
        /// This method intercepts requests to the WebSocket path and manages the entire lifecycle
        /// of WebSocket connections for real-time audio transcription.
        /// </summary>
        /// <param name="context">The HTTP context for the current request</param>
        public async Task InvokeAsync(HttpContext context)
        {
            // Check if this request is for the WebSocket endpoint
            if (context.Request.Path == Constants.WebSocketPath)
            {
                // Ensure this is actually a WebSocket upgrade request
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    _logger.LogWarning("Non-WebSocket request received at WebSocket endpoint");
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                // Extract required headers for call identification
                // The correlation ID is used to match the WebSocket connection with the active call
                if (!context.Request.Headers.TryGetValue("x-ms-call-correlation-id", out var correlationId) ||
                    string.IsNullOrEmpty(correlationId))
                {
                    _logger.LogWarning("WebSocket connection attempt missing correlation ID");
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                // Extract call connection ID to get the call media instance
                var callConnectionId = context.Request.Headers["x-ms-call-connection-id"].FirstOrDefault();
                var callMedia = callConnectionId != null ? _client.GetCallConnection(callConnectionId)?.GetCallMedia() : null;
                
                // Create a logging scope for better traceability of this specific call
                using (_logger.BeginScope("Call {CallConnectionId} with Correlation ID {CorrelationId}", callConnectionId, correlationId))
                {
                    _logger.LogInformation("WebSocket connection established");
                    
                    // Try to find the conversation ID from the call store
                    string? conversationId = null;
                    if (correlationId.Count > 0 && _callStore.TryGetValue(correlationId.ToString(), out var callContext))
                    {
                        conversationId = callContext.ConversationId;
                    }
                    else
                    {
                        _logger.LogWarning("No call context found for correlation ID: {CorrelationId}", correlationId.ToString());
                    }

                    // Accept the WebSocket connection and start processing
                    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    try
                    {
                        // Buffer for accumulating partial messages that span multiple WebSocket frames
                        string partialData = "";

                        // Main WebSocket message processing loop
                        while (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseSent)
                        {
                            // Rent buffer from shared pool for memory efficiency (4KB chunks)
                            byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(4096);
                            
                            // Set a 20-minute timeout for WebSocket operations
                            var cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(1200)).Token;
                            
                            WebSocketReceiveResult receiveResult = await webSocket.ReceiveAsync(
                                new ArraySegment<byte>(receiveBuffer, 0, 4096), 
                                cancellationToken);

                            // Process data messages (ignore close messages)
                            if (receiveResult.MessageType != WebSocketMessageType.Close)
                            {
                                // Convert received bytes to UTF-8 string
                                string data = Encoding.UTF8.GetString(receiveBuffer, 0, receiveResult.Count);

                                try
                                {
                                    // Check if this completes a message (WebSocket messages can be fragmented)
                                    if (receiveResult.EndOfMessage)
                                    {
                                        // Combine any partial data with the current chunk
                                        data = partialData + data;
                                        partialData = ""; // Reset partial buffer

                                        if (data != null)
                                        {
                                            // Handle intermediate transcription results for faster response
                                            if (data.Contains("Intermediate"))
                                            {
                                                _logger.LogDebug("Intermediate transcription received, checking for early processing");
                                                
                                                // Cancel current prompts to allow immediate response
                                                if (callMedia != null)
                                                    await callMedia.CancelAllMediaOperationsAsync();

                                                // Parse intermediate results for fast mode processing
                                                var streamingData = StreamingData.Parse(data);
                                                if (streamingData is TranscriptionData transcriptionData && 
                                                    !string.IsNullOrEmpty(transcriptionData.Text) &&
                                                    transcriptionData.Text.Length > 10) // Only process substantial intermediate text
                                                {
                                                    // Get conversation ID for fast processing
                                                    if (conversationId == null && correlationId.Count > 0 && 
                                                        _callStore.TryGetValue(correlationId.ToString(), out var ctx))
                                                    {
                                                        conversationId = ctx.ConversationId;
                                                    }

                                                    // Send intermediate result for very responsive conversations
                                                    if (!string.IsNullOrEmpty(conversationId))
                                                    {
                                                        await _callAutomationService.SendMessageAsync(conversationId, transcriptionData.Text);
                                                        _logger.LogInformation("Fast mode: Intermediate message sent: {TranscriptionText}", transcriptionData.Text);
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                // Parse the streaming data from Azure Communication Services
                                                var streamingData = StreamingData.Parse(data);
                                                
                                                // Handle transcription metadata (call setup information)
                                                if (streamingData is TranscriptionMetadata transcriptionMetadata)
                                                {
                                                    callMedia = _client.GetCallConnection(transcriptionMetadata.CallConnectionId)?.GetCallMedia();
                                                }
                                                
                                                // Handle actual transcription data (speech-to-text results)
                                                if (streamingData is TranscriptionData transcriptionData)
                                                {
                                                    _logger.LogDebug("Transcription data received: {TranscriptionText}", transcriptionData.Text);

                                                    // Only process final transcription results (not intermediate ones)
                                                    if (transcriptionData.ResultState == TranscriptionResultState.Final)
                                                    {
                                                        // Refresh conversation ID if needed
                                                        if (conversationId == null && correlationId.Count > 0 && 
                                                            _callStore.TryGetValue(correlationId.ToString(), out var ctx))
                                                        {
                                                            conversationId = ctx.ConversationId;
                                                        }

                                                        // Send the transcribed text to the bot conversation
                                                        if (!string.IsNullOrEmpty(conversationId))
                                                        {
                                                            await _callAutomationService.SendMessageAsync(conversationId, transcriptionData.Text);
                                                            _logger.LogInformation("Message sent to conversation: {TranscriptionText}", transcriptionData.Text);
                                                        }
                                                        else
                                                        {
                                                            _logger.LogWarning("Conversation Id is null, unable to send message");
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // Message is fragmented, accumulate the data
                                        partialData = partialData + data;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "WebSocket data processing error: {Message}", ex.Message);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "WebSocket connection error: {Message}", ex.Message);
                    }
                    finally
                    {
                        _logger.LogInformation("WebSocket connection closed");
                    }
                }
            }
            else
            {
                // Not a WebSocket request, pass to the next middleware in the pipeline
                await _next(context);
            }
        }
    }

    /// <summary>
    /// Extension methods for registering the WebSocket middleware in the application pipeline.
    /// </summary>
    public static class WebSocketMiddlewareExtensions
    {
        /// <summary>
        /// Adds the WebSocket middleware to the application's request pipeline.
        /// This enables WebSocket support for real-time call transcription.
        /// </summary>
        /// <param name="builder">The application builder to configure</param>
        /// <returns>The application builder for method chaining</returns>
        public static IApplicationBuilder UseCallWebSockets(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<WebSocketMiddleware>();
        }
    }
}