using Azure.Communication.CallAutomation;
using Microsoft.AspNetCore.Http;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Text;

namespace ACSforMCS.Middleware
{
    public class WebSocketMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<WebSocketMiddleware> _logger;
        private readonly CallAutomationClient _client;
        private readonly ConcurrentDictionary<string, CallContext> _callStore;
        private readonly Services.CallAutomationService _callAutomationService;

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

        public async Task InvokeAsync(HttpContext context)
        {
            if (context.Request.Path == Constants.WebSocketPath)
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    _logger.LogWarning("Non-WebSocket request received at WebSocket endpoint");
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                // Extract correlation ID and call connection ID
                if (!context.Request.Headers.TryGetValue("x-ms-call-correlation-id", out var correlationId) ||
                    string.IsNullOrEmpty(correlationId))
                {
                    _logger.LogWarning("WebSocket connection attempt missing correlation ID");
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                var callConnectionId = context.Request.Headers["x-ms-call-connection-id"].FirstOrDefault();
                var callMedia = callConnectionId != null ? _client.GetCallConnection(callConnectionId)?.GetCallMedia() : null;
                
                using (_logger.BeginScope("Call {CallConnectionId} with Correlation ID {CorrelationId}", callConnectionId, correlationId))
                {
                    _logger.LogInformation("WebSocket connection established");
                    
                    string? conversationId = null;
                    if (correlationId.Count > 0 && _callStore.TryGetValue(correlationId.ToString(), out var callContext))
                    {
                        conversationId = callContext.ConversationId;
                    }
                    else
                    {
                        _logger.LogWarning("No call context found for correlation ID: {CorrelationId}", correlationId.ToString());
                    }

                    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    try
                    {
                        string partialData = "";

                        while (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseSent)
                        {
                            byte[] receiveBuffer = new byte[4096];
                            var cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(1200)).Token;
                            WebSocketReceiveResult receiveResult = await webSocket.ReceiveAsync(
                                new ArraySegment<byte>(receiveBuffer), 
                                cancellationToken);

                            if (receiveResult.MessageType != WebSocketMessageType.Close)
                            {
                                string data = Encoding.UTF8.GetString(receiveBuffer, 0, receiveResult.Count);

                                try
                                {
                                    if (receiveResult.EndOfMessage)
                                    {
                                        data = partialData + data;
                                        partialData = "";

                                        if (data != null)
                                        {
                                            if (data.Contains("Intermediate"))
                                            {
                                                _logger.LogDebug("Intermediate transcription received, canceling prompt");
                                                if (callMedia != null)
                                                    await callMedia.CancelAllMediaOperationsAsync();
                                            }
                                            else
                                            {
                                                var streamingData = StreamingData.Parse(data);
                                                if (streamingData is TranscriptionMetadata transcriptionMetadata)
                                                {
                                                    callMedia = _client.GetCallConnection(transcriptionMetadata.CallConnectionId)?.GetCallMedia();
                                                }
                                                if (streamingData is TranscriptionData transcriptionData)
                                                {
                                                    _logger.LogDebug("Transcription data received: {TranscriptionText}", transcriptionData.Text);

                                                    if (transcriptionData.ResultState == TranscriptionResultState.Final)
                                                    {
                                                        if (conversationId == null && correlationId.Count > 0 && 
                                                            _callStore.TryGetValue(correlationId.ToString(), out var ctx))
                                                        {
                                                            conversationId = ctx.ConversationId;
                                                        }

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
                await _next(context);
            }
        }
    }

    // Extension method used to add the middleware to the HTTP request pipeline.
    public static class WebSocketMiddlewareExtensions
    {
        public static IApplicationBuilder UseCallWebSockets(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<WebSocketMiddleware>();
        }
    }
}