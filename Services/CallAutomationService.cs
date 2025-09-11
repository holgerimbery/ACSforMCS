using Azure.Communication.CallAutomation;
using Azure.Communication; 
using ACSforMCS.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.IO;
using Newtonsoft.Json;

namespace ACSforMCS.Services
{
    /// <summary>
    /// Core service that orchestrates Azure Communication Services call automation with Microsoft Bot Framework.
    /// This service handles the complete lifecycle of voice-enabled conversations including:
    /// - Starting DirectLine conversations with bots
    /// - Managing real-time WebSocket connections for bot responses
    /// - Converting text responses to speech and playing them to callers
    /// - Processing call transfers and conversation management
    /// - Coordinating between ACS call automation and bot framework APIs
    /// </summary>
    public class CallAutomationService
    {
        #region Private Fields
        
        /// <summary>
        /// Azure Communication Services client for call automation operations
        /// </summary>
        private readonly CallAutomationClient _client;
        
        /// <summary>
        /// Factory for creating HTTP clients with proper configuration for DirectLine API calls
        /// </summary>
        private readonly IHttpClientFactory _httpClientFactory;
        
        /// <summary>
        /// Logger for tracking service operations and debugging
        /// </summary>
        private readonly ILogger<CallAutomationService> _logger;
        
        /// <summary>
        /// Base URI for the application (used for webhook callbacks and WebSocket endpoints)
        /// </summary>
        private readonly string _baseUri;
        
        /// <summary>
        /// WebSocket URI derived from base URI for real-time communication
        /// </summary>
        private readonly string _baseWssUri;
        
        /// <summary>
        /// Voice synthesis options (language, voice name) for text-to-speech operations
        /// </summary>
        private readonly VoiceOptions _voiceOptions;
        
        /// <summary>
        /// Application settings containing connection strings and configuration
        /// </summary>
        private readonly AppSettings _appSettings;
        
        /// <summary>
        /// Thread-safe store for managing active call contexts and their state
        /// </summary>
        private readonly ConcurrentDictionary<string, CallContext> _callStore;
        
        /// <summary>
        /// In-memory cache for storing frequently used SSML templates to improve performance
        /// </summary>
        private readonly IMemoryCache _memoryCache;
        
        /// <summary>
        /// Thread-safe store for managing cancellation tokens per call for proper cleanup
        /// </summary>
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokenSources = new ConcurrentDictionary<string, CancellationTokenSource>();

        /// <summary>
        /// Compiled regex patterns for better performance when removing references from bot responses
        /// </summary>
        private static readonly Regex InlineRefRegex = new(@"\[\d+\]", RegexOptions.Compiled);
        private static readonly Regex RefListRegex = new(@"\n\[\d+\]:.*\n?", RegexOptions.Compiled | RegexOptions.Multiline);

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of CallAutomationService with all required dependencies.
        /// </summary>
        /// <param name="client">Azure Communication Services CallAutomation client</param>
        /// <param name="httpClientFactory">Factory for creating HTTP clients</param>
        /// <param name="logger">Logger for service operations</param>
        /// <param name="appSettings">Application configuration settings</param>
        /// <param name="voiceOptions">Voice synthesis configuration</param>
        /// <param name="callStore">Shared call context storage</param>
        public CallAutomationService(
            CallAutomationClient client,
            IHttpClientFactory httpClientFactory,
            ILogger<CallAutomationService> logger,
            IOptions<AppSettings> appSettings,
            IOptions<VoiceOptions> voiceOptions,
            ConcurrentDictionary<string, CallContext> callStore,
            IMemoryCache memoryCache)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _voiceOptions = voiceOptions?.Value ?? throw new ArgumentNullException(nameof(voiceOptions));
            _appSettings = appSettings?.Value ?? throw new ArgumentNullException(nameof(appSettings));
            _callStore = callStore ?? throw new ArgumentNullException(nameof(callStore));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            
            // Prepare URI configurations for webhooks and WebSocket connections
            _baseUri = _appSettings.BaseUri?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(_appSettings.BaseUri));
            _baseWssUri = _baseUri.StartsWith("https://") ? _baseUri.Substring("https://".Length) : _baseUri;
        }

        #endregion

        #region DirectLine Conversation Management

        /// <summary>
        /// Initiates a new conversation with the Bot Framework using DirectLine API.
        /// This method uses the configured DirectLine secret to authenticate and start a conversation
        /// that will be used for the duration of the call.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        /// <returns>A Conversation object containing the conversation ID and WebSocket URL</returns>
        /// <exception cref="HttpRequestException">Thrown when DirectLine API returns an error</exception>
        /// <exception cref="InvalidOperationException">Thrown when response cannot be deserialized</exception>
        public async Task<Conversation> StartConversationAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Use the pre-configured HTTP client with DirectLine authentication
                using var httpClient = _httpClientFactory.CreateClient("DirectLine");
                
                // Set up proper headers for DirectLine API communication
                httpClient.DefaultRequestHeaders.Accept.Clear();
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                
                // Create empty JSON content as required by DirectLine API
                var content = new StringContent("{}", Encoding.UTF8, "application/json");
                
                _logger.LogDebug("Starting DirectLine conversation with URL: {BaseAddress}", 
                    httpClient.BaseAddress?.ToString() ?? "null");
                
                // Log authorization status without exposing the actual token
                var hasAuthHeader = httpClient.DefaultRequestHeaders.Authorization != null;
                _logger.LogDebug("Authorization header present: {HasAuthHeader}", hasAuthHeader);
                
                // Make the API call to start a new conversation
                var response = await httpClient.PostAsync("conversations", content, cancellationToken);
                
                _logger.LogDebug("DirectLine API response status: {StatusCode}", response.StatusCode);
                
                // Handle API errors with detailed logging
                if (!response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("DirectLine API error response: {StatusCode}, Content: {Content}", 
                        response.StatusCode, responseContent);
                    throw new HttpRequestException($"DirectLine API returned {response.StatusCode}: {responseContent}");
                }
                
                // Parse the successful response
                var responseString = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("DirectLine API response: {Content}", responseString);
                
                var conversation = JsonConvert.DeserializeObject<Conversation>(responseString);
                if (conversation == null)
                {
                    throw new InvalidOperationException("Failed to deserialize conversation response");
                }
                
                _logger.LogInformation("Successfully started conversation with ID: {ConversationId}", 
                    conversation.ConversationId);
                
                return conversation;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Communication error with DirectLine API: {Message}", ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in StartConversationAsync: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Alternative conversation start method that first obtains a token, then uses it for authentication.
        /// This method is useful for scenarios where token-based authentication is preferred over direct secret usage.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        /// <returns>A Conversation object containing the conversation ID and WebSocket URL</returns>
        public async Task<Conversation> StartConversationWithTokenAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // First, obtain a temporary token from DirectLine
                var token = await GetDirectLineTokenAsync(cancellationToken);
                
                // Create a new HTTP client specifically for token-based authentication
                using var httpClient = new HttpClient();
                httpClient.BaseAddress = new Uri(Constants.DirectLineBaseUrl);
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                
                var content = new StringContent("{}", Encoding.UTF8, "application/json");
                
                var response = await httpClient.PostAsync("conversations", content, cancellationToken);
                
                if (!response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("Failed to start conversation with token: {StatusCode}, Content: {Content}", 
                        response.StatusCode, responseContent);
                    throw new HttpRequestException($"DirectLine API returned {response.StatusCode} with token auth");
                }
                
                var responseString = await response.Content.ReadAsStringAsync(cancellationToken);
                var conversation = JsonConvert.DeserializeObject<Conversation>(responseString);
                
                if (conversation == null)
                {
                    throw new InvalidOperationException("Failed to deserialize conversation response");
                }
                
                _logger.LogInformation("Successfully started conversation using token method, ID: {ConversationId}", 
                    conversation.ConversationId);
                
                return conversation;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start conversation with token method: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Sends a user message to an active DirectLine conversation.
        /// This method is typically called when speech-to-text conversion produces user input
        /// that needs to be forwarded to the bot.
        /// </summary>
        /// <param name="conversationId">The ID of the active conversation</param>
        /// <param name="message">The user's message text to send to the bot</param>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        /// <returns>True if the message was sent successfully, false otherwise</returns>
        public async Task<bool> SendMessageAsync(string conversationId, string message, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(conversationId))
            {
                throw new ArgumentNullException(nameof(conversationId));
            }

            if (string.IsNullOrEmpty(message))
            {
                _logger.LogWarning("Empty message not sent to conversation {ConversationId}", conversationId);
                return false;
            }

            try
            {
                using var httpClient = _httpClientFactory.CreateClient("DirectLine");
                
                // Create a Bot Framework activity message
                var messagePayload = new
                {
                    type = Constants.MessageActivityType,
                    from = new { id = Constants.DefaultUserName },
                    text = message
                };
                
                string messageJson = JsonConvert.SerializeObject(messagePayload);
                StringContent content = new StringContent(messageJson, Encoding.UTF8, "application/json");

                // Send the message to the specific conversation
                var response = await httpClient.PostAsync(
                    $"conversations/{conversationId}/activities", 
                    content,
                    cancellationToken);
                    
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send message to conversation {ConversationId}: {Message}", conversationId, ex.Message);
                return false;
            }
        }

        #endregion

        #region WebSocket Bot Communication

        /// <summary>
        /// Establishes a WebSocket connection to listen for real-time bot responses and processes them.
        /// This method handles the complete lifecycle of bot communication including:
        /// - Connecting to the bot's WebSocket stream
        /// - Processing different types of bot activities (messages, transfers, end-of-conversation)
        /// - Converting text responses to speech and playing them to the caller
        /// - Managing heartbeat to keep the connection alive
        /// </summary>
        /// <param name="streamUrl">The WebSocket URL provided by the DirectLine conversation</param>
        /// <param name="callConnection">The active ACS call connection for playing audio</param>
        /// <param name="cancellationToken">Cancellation token for stopping the listener</param>
        public async Task ListenToBotWebSocketAsync(string streamUrl, CallConnection callConnection, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(streamUrl))
            {
                _logger.LogWarning("WebSocket streaming is not enabled for this MCS Agent.");
                return;
            }

            using var webSocket = new ClientWebSocket();
            webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30); // Keep connection alive
            
            try
            {
                await webSocket.ConnectAsync(new Uri(streamUrl), cancellationToken);
                _logger.LogInformation("Connected to bot WebSocket at {StreamUrl}", streamUrl);

                // Set up periodic heartbeat to prevent connection timeouts
                using var heartbeatTimer = new Timer(
                    async _ => {
                        try {
                            if (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                            {
                                var heartbeatMsg = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
                                await webSocket.SendAsync(
                                    new ArraySegment<byte>(heartbeatMsg), 
                                    WebSocketMessageType.Text, 
                                    true, 
                                    cancellationToken);
                                _logger.LogDebug("Sent heartbeat ping");
                            }
                        }
                        catch (Exception ex) when (!(ex is TaskCanceledException || ex is OperationCanceledException))
                        {
                            _logger.LogWarning(ex, "Error sending heartbeat: {Message}", ex.Message);
                        }
                    },
                    null,
                    TimeSpan.FromSeconds(45),  // Initial delay
                    TimeSpan.FromSeconds(45)); // Repeat interval

                var buffer = new byte[4096];
                var messageBuilder = new StringBuilder();

                // Main message processing loop
                while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    messageBuilder.Clear(); // Reset for each new message
                    WebSocketReceiveResult result;
                    try
                    {
                        // Receive complete WebSocket message (may be fragmented across multiple frames)
                        do
                        {
                            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                            messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        } while (!result.EndOfMessage);

                        string rawMessage = messageBuilder.ToString();
                        
                        // Conservative filtering - only skip obviously non-actionable messages
                        if (string.IsNullOrWhiteSpace(rawMessage) || 
                            !rawMessage.Contains("activities"))
                        {
                            _logger.LogTrace("Skipping non-activity message from WebSocket");
                            continue;
                        }
                        
                        // Parse the bot's response to extract actionable content
                        var agentActivity = ExtractLatestAgentActivity(rawMessage);

                        // Handle different types of bot responses
                        
                        // Error responses - provide user-friendly feedback based on error type
                        if (agentActivity.Type == Constants.ErrorActivityType)
                        {
                            // Only log actual errors, not silent parsing issues
                            if (agentActivity.Text != "SILENT_PARSING_ERROR")
                            {
                                _logger.LogWarning("Caught error response: {ErrorText}", agentActivity.Text);
                            }
                            else
                            {
                                _logger.LogDebug("Silent parsing error (normal WebSocket metadata)");
                            }
                            
                            // Only play audio for specific, actionable errors - not generic parsing issues
                            try
                            {
                                if (!string.IsNullOrEmpty(agentActivity.Text) && 
                                    (agentActivity.Text.Contains("authentication") || agentActivity.Text.Contains("authorization")))
                                {
                                    await PlayToAllAsync(callConnection.GetCallMedia(), 
                                        "I'm having trouble connecting to the service. Please wait a moment.", 
                                        cancellationToken);
                                }
                                else if (!string.IsNullOrEmpty(agentActivity.Text) && agentActivity.Text.Contains("timeout"))
                                {
                                    await PlayToAllAsync(callConnection.GetCallMedia(), 
                                        "I need a bit more time to process that. One moment please.", 
                                        cancellationToken);
                                }
                                // For generic "Something went wrong" and parsing errors, don't play anything
                                // These are usually system-level parsing issues, not user-facing problems
                                else
                                {
                                    _logger.LogDebug("Silently ignoring generic error message: {ErrorText}", agentActivity.Text);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Could not play error message, call may have ended: {Message}", ex.Message);
                            }
                            
                            continue;
                        }

                        // Regular message responses - convert to speech and play to caller
                        if (agentActivity.Type == Constants.MessageActivityType && !string.IsNullOrEmpty(agentActivity.Text))
                        {
                            // Filter out generic error messages that shouldn't be played to users
                            if (agentActivity.Text.Contains("An error has occurred") || 
                                agentActivity.Text.Contains("error has occurred"))
                            {
                                _logger.LogWarning("Filtered out generic error message from bot: {ErrorText}", agentActivity.Text);
                                continue;
                            }
                            
                            _logger.LogInformation("Playing Agent Response: {AgentText}", agentActivity.Text);
                            try 
                            {
                                await PlayToAllAsync(callConnection.GetCallMedia(), agentActivity.Text ?? string.Empty, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error playing message to user: {Message}", ex.Message);
                                // Continue processing - don't expose technical errors to the user
                            }
                        }
                        // Transfer requests - initiate call transfer to specified number
                        else if (agentActivity.Type == Constants.TransferActivityType && !string.IsNullOrEmpty(agentActivity.Text))
                        {
                            var transferParts = agentActivity.Text.Split('|');
                            var phoneNumber = transferParts[0];
                            var transferMessage = transferParts.Length > 1 ? transferParts[1] : "Please hold while I transfer your call.";
                            
                            _logger.LogInformation("Executing call transfer to: {PhoneNumber}", phoneNumber);
                            
                            try
                            {
                                var transferSuccess = await AnnounceAndTransferCallAsync(
                                    callConnection, 
                                    phoneNumber, 
                                    transferMessage, 
                                    cancellationToken);
                                    
                                if (!transferSuccess)
                                {
                                    try
                                    {
                                        await PlayToAllAsync(
                                            callConnection.GetCallMedia(), 
                                            "I'm sorry, I couldn't transfer your call right now. Please try again later.", 
                                            cancellationToken);
                                    }
                                    catch (Exception playEx)
                                    {
                                        _logger.LogWarning(playEx, "Could not play transfer failure message, call may have ended");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error during call transfer: {Message}", ex.Message);
                                try
                                {
                                    await PlayToAllAsync(
                                        callConnection.GetCallMedia(), 
                                        "I'm sorry, there was an issue with the transfer. Please try again.", 
                                        cancellationToken);
                                }
                                catch (Exception playEx)
                                {
                                    _logger.LogWarning(playEx, "Could not play transfer error message, call may have ended");
                                }
                            }
                        }
                        // End conversation signal - terminate the call gracefully
                        else if (agentActivity.Type == Constants.EndOfConversationActivityType)
                        {
                            _logger.LogInformation("End of Conversation signal received, hanging up call");
                            await callConnection.HangUpAsync(true, cancellationToken: cancellationToken);
                        }
                    }
                    catch (Exception ex) when (!(ex is TaskCanceledException || ex is OperationCanceledException))
                    {
                        // Log unexpected exceptions but continue processing
                        _logger.LogError(ex, "Error processing WebSocket message: {Message}", ex.Message);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // Expected during cancellation - not an error condition
                _logger.LogInformation("WebSocket connection setup canceled - this is normal during disconnection");
            }
            catch (OperationCanceledException)
            {
                // Also expected during cancellation - not an error condition
                _logger.LogInformation("WebSocket connection setup canceled - this is normal during disconnection");
            }
            catch (Exception ex)
            {
                // Log only truly unexpected errors
                _logger.LogError(ex, "Bot WebSocket error: {Message}", ex.Message);
            }
            finally
            {
                // Ensure clean WebSocket closure
                if (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        // Use separate cancellation token to avoid issues during cleanup
                        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cleanupCts.Token);
                        _logger.LogInformation("Bot WebSocket connection closed gracefully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation("Non-critical error during WebSocket closure: {Message}", ex.Message);
                    }
                }
            }
        }

        #endregion

        #region Audio Playback

        /// <summary>
        /// Converts text to speech using SSML and plays it to all participants in the call.
        /// This method uses Azure Cognitive Services for text-to-speech synthesis with the configured voice.
        /// </summary>
        /// <param name="callConnectionMedia">The call media instance for audio operations</param>
        /// <param name="message">The text message to convert to speech and play</param>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        public async Task PlayToAllAsync(CallMedia callConnectionMedia, string message, CancellationToken cancellationToken = default)
        {
            try
            {
                // Generate cache key based on message, voice name, and language for SSML caching
                var cacheKey = $"ssml_{_voiceOptions.VoiceName}_{_voiceOptions.Language}_{message.GetHashCode()}";
                
                // Try to get cached SSML, or create and cache it if not found
                var ssml = _memoryCache.GetOrCreate(cacheKey, entry =>
                {
                    // Set cache expiration to 1 hour for performance while allowing updates
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                    entry.SlidingExpiration = TimeSpan.FromMinutes(30);
                    
                    // Create SSML (Speech Synthesis Markup Language) for better voice control
                    return $@"<speak version=""1.0"" xmlns=""http://www.w3.org/2001/10/synthesis"" xml:lang=""{_voiceOptions.Language}"">
                <voice name=""{_voiceOptions.VoiceName}"">{message}</voice>
            </speak>";
                });
            
                var ssmlPlaySource = new SsmlSource(ssml);
                var playOptions = new PlayToAllOptions(ssmlPlaySource)
                {
                    OperationContext = Constants.DefaultOperationContext
                };

                await callConnectionMedia.PlayToAllAsync(playOptions, cancellationToken);
            }
            catch (Azure.RequestFailedException ex) when (ex.ErrorCode == "8501")
            {
                // Call is not in established state - this is expected during transfers or call ending
                _logger.LogDebug("Cannot play audio, call not in established state: {Message}", ex.Message);
                throw; // Re-throw so caller can handle appropriately
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to play message to call: {Message}", ex.Message);
                throw;
            }
        }

        #endregion

        #region Call Transfer Operations

        /// <summary>
        /// Transfers the current call to an external phone number using ACS built-in transfer functionality.
        /// This method handles the technical aspects of call transfer within the Azure Communication Services platform.
        /// </summary>
        /// <param name="callConnection">The current call connection to transfer</param>
        /// <param name="targetPhoneNumber">The phone number to transfer to (must be in E.164 format, e.g., "+1234567890")</param>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        /// <returns>True if transfer was initiated successfully, false if it failed</returns>
        public async Task<bool> TransferCallToPhoneNumberAsync(
            CallConnection callConnection, 
            string targetPhoneNumber, 
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Validate the phone number format (must be E.164)
                if (string.IsNullOrEmpty(targetPhoneNumber) || !targetPhoneNumber.StartsWith("+"))
                {
                    _logger.LogError("Invalid phone number format: {PhoneNumber}. Must be in E.164 format", targetPhoneNumber);
                    return false;
                }

                _logger.LogInformation("Processing direct transfer to: {PhoneNumber}", targetPhoneNumber);
                
                var targetParticipant = new PhoneNumberIdentifier(targetPhoneNumber);
                
                // Use ACS's built-in transfer functionality
                var transferOptions = new TransferToParticipantOptions(targetParticipant)
                {
                    OperationContext = "DirectTransfer"
                };
                
                await callConnection.TransferCallToParticipantAsync(transferOptions, cancellationToken);
                
                _logger.LogInformation("Successfully initiated direct transfer to {PhoneNumber}", targetPhoneNumber);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Direct transfer failed: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Announces the transfer to the caller with contextual information and then processes the transfer.
        /// This method provides a better user experience by informing the caller about what's happening
        /// before the actual transfer occurs.
        /// </summary>
        /// <param name="callConnection">The current call connection</param>
        /// <param name="targetPhoneNumber">The phone number to transfer to</param>
        /// <param name="customMessage">Optional custom message to announce before transfer</param>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        /// <returns>True if the announcement and transfer were successful</returns>
        public async Task<bool> AnnounceAndTransferCallAsync(
            CallConnection callConnection,
            string targetPhoneNumber,
            string? customMessage = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var callMedia = callConnection.GetCallMedia();
                
                // Create an informative transfer message
                var transferMessage = customMessage ?? 
                    "Thank you for your call. I'm connecting you to one of our specialists who will be able to assist you further. Please hold while I transfer your call.";
                
                // Announce the transfer to the caller
                await PlayToAllAsync(callMedia, transferMessage, cancellationToken);
                await Task.Delay(3000, cancellationToken); // Give time for the message to play
                
                // Execute the actual transfer
                var transferSuccess = await TransferCallToPhoneNumberAsync(callConnection, targetPhoneNumber, cancellationToken);
                
                if (transferSuccess)
                {
                    // Inform the user about the transfer process
                    // Note: In production, you might implement different transfer strategies:
                    // - Supervised transfer (add agent as participant, then remove bot)
                    // - Blind transfer (transfer immediately)
                    // - Callback transfer (hang up and have system call both parties)
                    
                    await PlayToAllAsync(callMedia, 
                        "Your call is being transferred. If you are disconnected, an agent will call you back within a few minutes.", 
                        cancellationToken);
                    
                    await Task.Delay(2000, cancellationToken);
                    return true;
                }
                else
                {
                    // Inform user of transfer failure
                    await PlayToAllAsync(callMedia, 
                        "I'm sorry, but I'm unable to transfer your call at this time. Please try calling back later.", 
                        cancellationToken);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Transfer announcement failed: {Message}", ex.Message);
                return false;
            }
        }

        #endregion

        #region Message Processing

        /// <summary>
        /// Extracts and parses the latest agent activity from a DirectLine WebSocket message.
        /// This method handles the complex JSON structure of Bot Framework messages and extracts
        /// actionable content including regular messages, transfer commands, and conversation control signals.
        /// Optimized for performance with System.Text.Json and pattern matching.
        /// </summary>
        /// <param name="rawMessage">The raw JSON message received from DirectLine WebSocket</param>
        /// <returns>An AgentActivity object containing the parsed activity type and content</returns>
        public AgentActivity ExtractLatestAgentActivity(string rawMessage)
        {
            // Early validation to prevent unnecessary JSON parsing overhead
            if (string.IsNullOrWhiteSpace(rawMessage))
            {
                _logger.LogDebug("Received null or empty message from DirectLine WebSocket");
                return CreateSilentError();
            }

            // Basic format validation to avoid JSON parsing for obviously invalid messages  
            var trimmed = rawMessage.TrimStart();
            if (!trimmed.StartsWith('{'))
            {
                _logger.LogTrace("Message doesn't start with JSON object, skipping");
                return CreateSilentError();
            }

            try
            {
                // Log a sample of the message for debugging (truncate if too long to avoid log spam)
                if (rawMessage.Length > 500)
                {
                    _logger.LogDebug("Attempting to extract agent activity from message: {SampleMessage}", 
                        rawMessage.AsSpan(0, 500).ToString() + "...");
                }
                else
                {
                    _logger.LogDebug("Attempting to extract agent activity from message: {SampleMessage}", rawMessage);
                }
                
                using var doc = JsonDocument.Parse(rawMessage);

                // Validate the message structure
                if (!doc.RootElement.TryGetProperty("activities", out var activities) ||
                    activities.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning("Invalid message structure: missing or invalid 'activities' property");
                    return CreateSilentError();
                }

                var activityCount = activities.GetArrayLength();
                _logger.LogDebug("Found {Count} activities in the message", activityCount);
                
                if (activityCount == 0)
                {
                    _logger.LogWarning("The activities array is empty");
                    return CreateSilentError();
                }

                // Process activities in reverse order to get the most recent agent response
                // Use Span for better performance when iterating
                var activitiesArray = activities.EnumerateArray().ToArray();
                for (int i = activitiesArray.Length - 1; i >= 0; i--)
                {
                    var activity = activitiesArray[i];
                    
                    if (TryProcessActivity(activity, i, out var result))
                    {
                        return result;
                    }
                }

                _logger.LogDebug("No valid agent activity found in message, returning silent error response");
                return CreateSilentError();
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogDebug(ex, "JSON parsing error: {Message}", ex.Message);
                return CreateSilentError();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in ExtractLatestAgentActivity: {Message}", ex.Message);
                return CreateSilentError();
            }
        }

        /// <summary>
        /// Tries to process a single activity and extract actionable content.
        /// Uses pattern matching for better performance and readability.
        /// </summary>
        /// <param name="activity">The JSON activity element to process</param>
        /// <param name="index">The index of the activity for logging</param>
        /// <param name="result">The extracted activity result</param>
        /// <returns>True if a valid activity was extracted, false otherwise</returns>
        private bool TryProcessActivity(JsonElement activity, int index, out AgentActivity result)
        {
            result = new AgentActivity();
            
            if (!activity.TryGetProperty("type", out var typeElement))
            {
                _logger.LogDebug("Activity at index {Index} has no 'type' property", index);
                return false;
            }

            var typeValue = typeElement.GetString();
            _logger.LogDebug("Activity at index {Index} has type: {Type}", index, typeValue);

            // Use pattern matching for better performance
            return typeValue switch
            {
                Constants.MessageActivityType => TryProcessMessageActivity(activity, index, out result),
                Constants.EndOfConversationActivityType => TryProcessEndOfConversation(out result),
                "transfer" => TryProcessTransferActivity(activity, out result),
                _ => false
            };
        }

        /// <summary>
        /// Processes message activities from the bot
        /// </summary>
        private bool TryProcessMessageActivity(JsonElement activity, int index, out AgentActivity result)
        {
            result = new AgentActivity();
            
            if (!activity.TryGetProperty("from", out var from) ||
                !from.TryGetProperty("id", out var fromId))
            {
                _logger.LogDebug("Message activity at index {Index} has no valid 'from' property", index);
                return false;
            }

            var fromIdValue = fromId.GetString();
            _logger.LogDebug("Message activity at index {Index} is from: {FromId}", index, fromIdValue);

            // Skip user messages - we only want agent responses
            if (fromIdValue == Constants.DefaultUserName)
            {
                _logger.LogDebug("Message activity at index {Index} is from user, not agent", index);
                return false;
            }

            // Check for transfer command in text format (TRANSFER:phonenumber:message)
            if (activity.TryGetProperty("text", out var textElement))
            {
                var textContent = textElement.GetString();
                if (!string.IsNullOrEmpty(textContent) && textContent.StartsWith("TRANSFER:", StringComparison.OrdinalIgnoreCase))
                {
                    return TryParseTransferCommand(textContent, out result);
                }
            }

            // Check for voice content first (preferred for speech synthesis)
            if (activity.TryGetProperty("speak", out var speak))
            {
                var speakContent = speak.GetString();
                if (!string.IsNullOrEmpty(speakContent))
                {
                    _logger.LogDebug("Voice content received: {SpeakContent}", speakContent);
                    result = new AgentActivity()
                    {
                        Type = Constants.MessageActivityType,
                        Text = RemoveReferences(speakContent)
                    };
                    return true;
                }
            }

            // Fall back to text content
            if (activity.TryGetProperty("text", out var text))
            {
                var textContent = text.GetString();
                if (!string.IsNullOrEmpty(textContent))
                {
                    _logger.LogDebug("Text content received: {TextContent}", textContent);

                    // Detect potential error messages in the content
                    var activityType = IsErrorMessage(textContent) ? Constants.ErrorActivityType : Constants.MessageActivityType;
                    
                    result = new AgentActivity()
                    {
                        Type = activityType,
                        Text = RemoveReferences(textContent)
                    };
                    return true;
                }
            }

            _logger.LogDebug("Message activity at index {Index} has neither 'speak' nor 'text' property", index);
            return false;
        }

        /// <summary>
        /// Processes end of conversation activities
        /// </summary>
        private bool TryProcessEndOfConversation(out AgentActivity result)
        {
            _logger.LogInformation("EndOfConversation activity received");
            result = new AgentActivity()
            {
                Type = Constants.EndOfConversationActivityType
            };
            return true;
        }

        /// <summary>
        /// Processes structured transfer activities
        /// </summary>
        private bool TryProcessTransferActivity(JsonElement activity, out AgentActivity result)
        {
            result = new AgentActivity();
            
            if (!activity.TryGetProperty("value", out var transferValue))
            {
                return false;
            }
            
            var phoneNumber = transferValue.TryGetProperty("phoneNumber", out var phoneNumberProp) 
                ? phoneNumberProp.GetString() : null;
            var transferMessage = transferValue.TryGetProperty("message", out var messageProp) 
                ? messageProp.GetString() : "Please hold while I transfer your call.";
            
            if (!string.IsNullOrEmpty(phoneNumber))
            {
                _logger.LogInformation("Structured transfer command received for phone number: {PhoneNumber}", phoneNumber);
                result = new AgentActivity()
                {
                    Type = Constants.TransferActivityType,
                    Text = $"{phoneNumber}|{transferMessage}"
                };
                return true;
            }
            
            return false;
        }

        /// <summary>
        /// Parses transfer commands from text content
        /// </summary>
        private bool TryParseTransferCommand(string textContent, out AgentActivity result)
        {
            result = new AgentActivity();
            
            var parts = textContent.Split(':', 3);
            if (parts.Length >= 2)
            {
                var phoneNumber = parts[1].Trim();
                var transferMessage = parts.Length >= 3 ? parts[2].Trim() : "Please hold while I transfer your call.";
                
                _logger.LogInformation("Transfer command received for phone number: {PhoneNumber}", phoneNumber);
                result = new AgentActivity()
                {
                    Type = Constants.TransferActivityType,
                    Text = $"{phoneNumber}|{transferMessage}"
                };
                return true;
            }
            
            return false;
        }

        /// <summary>
        /// Optimized check to determine if a raw WebSocket message likely contains bot activities
        /// without expensive JSON parsing. Uses string scanning for performance.
        /// </summary>
        /// <param name="rawMessage">Raw WebSocket message</param>
        /// <returns>True if message likely contains actionable bot activities</returns>
        private static bool ContainsActionableActivity(string rawMessage)
        {
            // Fast checks for common non-actionable message patterns
            if (rawMessage.Length < 100 || // Too short to contain meaningful activities
                rawMessage.Contains("\"watermark\"") || // DirectLine metadata
                rawMessage.Contains("\"streamUrl\"") || // Connection info
                rawMessage.Contains("\"heartbeat\"") || // Keepalive messages
                !rawMessage.Contains("\"activities\"")) // No activities array
            {
                return false;
            }
            
            // Check if message contains actual content (text OR speak)
            // Note: Most bot messages have "text", voice-optimized might have "speak"
            if (!rawMessage.Contains("\"text\"") && !rawMessage.Contains("\"speak\""))
            {
                return false;
            }
            
            return true;
        }

        /// <summary>
        /// Checks if a message content indicates an error
        /// </summary>
        private static bool IsErrorMessage(string textContent)
        {
            return textContent.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                   textContent.Contains("sorry", StringComparison.OrdinalIgnoreCase) ||
                   textContent.Contains("fail", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates a silent error response that won't be played to users
        /// </summary>
        private static AgentActivity CreateSilentError()
        {
            return new AgentActivity()
            {
                Type = Constants.ErrorActivityType,
                Text = "SILENT_PARSING_ERROR" // This won't be played to users
            };
        }

        /// <summary>
        /// Removes citation references from bot responses to make them more suitable for speech synthesis.
        /// Bot responses often include references like [1], [2] and reference lists that are not needed
        /// in voice conversations.
        /// </summary>
        /// <param name="input">The input text containing potential references</param>
        /// <returns>Cleaned text with references removed</returns>
        private static string RemoveReferences(string input)
        {
            // Remove inline references like [1], [2], etc.
            string withoutInlineRefs = Regex.Replace(input, @"\[\d+\]", "");

            // Remove reference list at the end (lines starting with [number]:)
            string withoutRefList = Regex.Replace(withoutInlineRefs, @"\n\[\d+\]:.*(\n|$)", "");

            return withoutRefList.Trim();
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Generates a unique callback URI for handling ACS webhook events.
        /// Each call gets a unique callback URL for proper event routing.
        /// </summary>
        /// <returns>A unique URI for webhook callbacks</returns>
        public Uri GetCallbackUri()
        {
            return new Uri(_baseUri + $"/api/calls/{Guid.NewGuid()}");
        }

        /// <summary>
        /// Generates the WebSocket URI for real-time audio streaming.
        /// This URI is used by ACS to establish WebSocket connections for streaming audio data.
        /// </summary>
        /// <returns>WebSocket URI for audio streaming</returns>
        public Uri GetTranscriptionTransportUri()
        {
            return new Uri($"wss://{_baseWssUri}/ws");
        }

        /// <summary>
        /// Cleans up resources associated with a specific call when it ends.
        /// This includes canceling ongoing operations and removing call context from storage.
        /// </summary>
        /// <param name="correlationId">The correlation ID of the call to clean up</param>
        public void CleanupCall(string correlationId)
        {
            // Cancel any ongoing operations for this call
            if (_cancellationTokenSources.TryRemove(correlationId, out var tokenSource))
            {
                tokenSource.Cancel();
                tokenSource.Dispose();
            }
            
            // Remove call context from storage
            _ = _callStore.TryRemove(correlationId, out _);
            _logger.LogInformation("Cleaned up resources for call with correlation ID {CorrelationId}", correlationId);
        }

        /// <summary>
        /// Registers a cancellation token source for a specific call.
        /// This allows for proper cleanup and cancellation of call-specific operations.
        /// </summary>
        /// <param name="correlationId">The correlation ID of the call</param>
        /// <param name="tokenSource">The cancellation token source to register</param>
        public void RegisterTokenSource(string correlationId, CancellationTokenSource tokenSource)
        {
            _cancellationTokenSources[correlationId] = tokenSource;
        }

        /// <summary>
        /// Obtains a temporary authentication token from DirectLine API.
        /// This method is used by the token-based authentication flow.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        /// <returns>A temporary DirectLine authentication token</returns>
        private async Task<string> GetDirectLineTokenAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var httpClient = _httpClientFactory.CreateClient("DirectLine");
                
                // Temporarily clear auth header and use secret in URL for token generation
                var originalAuth = httpClient.DefaultRequestHeaders.Authorization;
                httpClient.DefaultRequestHeaders.Authorization = null;
                
                var content = new StringContent("{}", Encoding.UTF8, "application/json");
                
                // Use token generation endpoint with secret as query parameter
                var response = await httpClient.PostAsync(
                    $"tokens/generate?secret={Uri.EscapeDataString(originalAuth?.Parameter ?? "")}", 
                    content, 
                    cancellationToken);
                
                // Restore the original auth header
                httpClient.DefaultRequestHeaders.Authorization = originalAuth;
                
                if (!response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("DirectLine token generation failed: {StatusCode}, Content: {Content}", 
                        response.StatusCode, responseContent);
                    throw new HttpRequestException($"DirectLine token generation failed: {response.StatusCode}");
                }
                
                var responseString = await response.Content.ReadAsStringAsync(cancellationToken);
                var tokenResponse = JsonConvert.DeserializeObject<DirectLineTokenResponse>(responseString);
                
                if (string.IsNullOrEmpty(tokenResponse?.Token))
                {
                    throw new InvalidOperationException("Failed to get DirectLine token from response");
                }
                
                _logger.LogDebug("Successfully obtained DirectLine token");
                return tokenResponse.Token;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get DirectLine token: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Data transfer object for DirectLine token API responses.
        /// </summary>
        private class DirectLineTokenResponse
        {
            public string? Token { get; set; }
            public int ExpiresIn { get; set; }
        }

        #endregion
    }
}
