using Azure.Communication.CallAutomation;
using ACSforMCS.Configuration;
using Microsoft.Extensions.Options;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using JsonException = Newtonsoft.Json.JsonException;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.IO;

namespace ACSforMCS.Services
{
    public class CallAutomationService
    {
        private readonly CallAutomationClient _client;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<CallAutomationService> _logger;
        private readonly string _baseUri;
        private readonly string _baseWssUri;
        private readonly VoiceOptions _voiceOptions;
        private readonly ConcurrentDictionary<string, CallContext> _callStore;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokenSources = new ConcurrentDictionary<string, CancellationTokenSource>();

        public CallAutomationService(
            CallAutomationClient client,
            IHttpClientFactory httpClientFactory,
            ILogger<CallAutomationService> logger,
            IOptions<AppSettings> appSettings,
            IOptions<VoiceOptions> voiceOptions,
            ConcurrentDictionary<string, CallContext> callStore)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _voiceOptions = voiceOptions?.Value ?? throw new ArgumentNullException(nameof(voiceOptions));
            _callStore = callStore ?? throw new ArgumentNullException(nameof(callStore));
            
            _baseUri = appSettings.Value.BaseUri?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(appSettings.Value.BaseUri));
            _baseWssUri = _baseUri.StartsWith("https://") ? _baseUri.Substring("https://".Length) : _baseUri;
        }

        public async Task<Conversation> StartConversationAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var httpClient = _httpClientFactory.CreateClient("DirectLine");
                
                // Add additional headers that might be required
                httpClient.DefaultRequestHeaders.Accept.Clear();
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                
                // Create an empty content with proper Content-Type
                var content = new StringContent("{}", Encoding.UTF8, "application/json");
                
                _logger.LogDebug("Starting DirectLine conversation with URL: {BaseAddress}", 
                    httpClient.BaseAddress?.ToString() ?? "null");
                
                // Check if Authorization header is present (don't log the full token)
                var hasAuthHeader = httpClient.DefaultRequestHeaders.Authorization != null;
                _logger.LogDebug("Authorization header present: {HasAuthHeader}", hasAuthHeader);
                
                // Use PostAsync with content (even though it's empty)
                var response = await httpClient.PostAsync("conversations", content, cancellationToken);
                
                _logger.LogDebug("DirectLine API response status: {StatusCode}", response.StatusCode);
                
                if (!response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("DirectLine API error response: {StatusCode}, Content: {Content}", 
                        response.StatusCode, responseContent);
                    throw new HttpRequestException($"DirectLine API returned {response.StatusCode}: {responseContent}");
                }
                
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

        // Alternative implementation that gets a token first
        public async Task<Conversation> StartConversationWithTokenAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Get a token first
                var token = await GetDirectLineTokenAsync(cancellationToken);
                
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
                
                var messagePayload = new
                {
                    type = Constants.MessageActivityType,
                    from = new { id = Constants.DefaultUserName },
                    text = message
                };
                
                string messageJson = JsonConvert.SerializeObject(messagePayload);
                StringContent content = new StringContent(messageJson, Encoding.UTF8, "application/json");

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

        public async Task ListenToBotWebSocketAsync(string streamUrl, CallConnection callConnection, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(streamUrl))
            {
                _logger.LogWarning("WebSocket streaming is not enabled for this MCS Agent.");
                return;
            }

            using var webSocket = new ClientWebSocket();
            try
            {
                await webSocket.ConnectAsync(new Uri(streamUrl), cancellationToken);
                _logger.LogInformation("Connected to bot WebSocket at {StreamUrl}", streamUrl);

                var buffer = new byte[4096];
                var messageBuilder = new StringBuilder();

                while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    messageBuilder.Clear(); // Reset buffer for each new message
                    WebSocketReceiveResult result;
                    try
                    {
                        do
                        {
                            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                            messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                        } while (!result.EndOfMessage); // Continue until we've received the full message

                        string rawMessage = messageBuilder.ToString();
                        var agentActivity = ExtractLatestAgentActivity(rawMessage);

                        if (agentActivity.Type == Constants.MessageActivityType && !string.IsNullOrEmpty(agentActivity.Text))
                        {
                            _logger.LogInformation("Playing Agent Response: {AgentText}", agentActivity.Text);
                            await PlayToAllAsync(callConnection.GetCallMedia(), agentActivity.Text ?? string.Empty, cancellationToken);
                        }
                        else if (agentActivity.Type == Constants.EndOfConversationActivityType)
                        {
                            _logger.LogInformation("End of Conversation signal received, hanging up call");
                            await callConnection.HangUpAsync(true, cancellationToken: cancellationToken);
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        // This is expected when the call is disconnected and tasks are canceled
                        _logger.LogInformation("WebSocket operation canceled - this is normal during disconnection");
                        break; // Exit the loop
                    }
                    catch (OperationCanceledException)
                    {
                        // Also expected during cancellation
                        _logger.LogInformation("WebSocket operation canceled - this is normal during disconnection");
                        break; // Exit the loop
                    }
                    catch (Exception ex) when (ex.InnerException is IOException || 
                                               ex.InnerException is SocketException)
                    {
                        // Network errors that can happen during disconnection
                        _logger.LogInformation("WebSocket network connection closed: {Message}", ex.Message);
                        break;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // Expected during cancellation, don't log as error
                _logger.LogInformation("WebSocket connection setup canceled - this is normal during disconnection");
            }
            catch (OperationCanceledException)
            {
                // Also expected during cancellation, don't log as error
                _logger.LogInformation("WebSocket connection setup canceled - this is normal during disconnection");
            }
            catch (Exception ex)
            {
                // Only unexpected errors should be logged as errors
                _logger.LogError(ex, "Bot WebSocket error: {Message}", ex.Message);
            }
            finally
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        // Use a new token to avoid cancellation exceptions during cleanup
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

        public async Task PlayToAllAsync(CallMedia callConnectionMedia, string message, CancellationToken cancellationToken = default)
        {
            var ssml = $@"<speak version=""1.0"" xmlns=""http://www.w3.org/2001/10/synthesis"" xml:lang=""{_voiceOptions.Language}"">
                <voice name=""{_voiceOptions.VoiceName}"">{message}</voice>
            </speak>";
            
            var ssmlPlaySource = new SsmlSource(ssml);
            var playOptions = new PlayToAllOptions(ssmlPlaySource)
            {
                OperationContext = Constants.DefaultOperationContext
            };

            await callConnectionMedia.PlayToAllAsync(playOptions, cancellationToken);
        }

        public AgentActivity ExtractLatestAgentActivity(string rawMessage)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawMessage);

                if (doc.RootElement.TryGetProperty("activities", out var activities) && 
                    activities.ValueKind == JsonValueKind.Array)
                {
                    // Iterate in reverse order to get the latest message
                    for (int i = activities.GetArrayLength() - 1; i >= 0; i--)
                    {
                        var activity = activities[i];

                        if (activity.TryGetProperty("type", out var type))
                        {
                            if (type.GetString() == Constants.MessageActivityType)
                            {
                                if (activity.TryGetProperty("from", out var from) &&
                                    from.TryGetProperty("id", out var fromId) &&
                                    fromId.GetString() != Constants.DefaultUserName) // Ensure message is from Agent
                                {
                                    if (activity.TryGetProperty("speak", out var speak))
                                    {
                                        _logger.LogDebug("Voice content received: {SpeakContent}", speak.GetString());
                                        return new AgentActivity()
                                        {
                                            Type = Constants.MessageActivityType,
                                            Text = RemoveReferences(speak.GetString() ?? string.Empty)
                                        };
                                    }

                                    if (activity.TryGetProperty("text", out var text))
                                    {
                                        return new AgentActivity()
                                        {
                                            Type = Constants.MessageActivityType,
                                            Text = RemoveReferences(text.GetString() ?? string.Empty)
                                        };
                                    }
                                }
                            }
                            else if(type.GetString() == Constants.EndOfConversationActivityType)
                            {
                                _logger.LogInformation("EndOfConversation activity received");
                                return new AgentActivity()
                                {
                                    Type = Constants.EndOfConversationActivityType
                                };
                            }
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Unexpected JSON format in agent activity: {Message}", ex.Message);
            }
            
            _logger.LogWarning("No valid agent activity found in message, returning default error response");
            return new AgentActivity()
            {
                Type = Constants.ErrorActivityType,
                Text = "Sorry, Something went wrong"
            };
        }

        private static string RemoveReferences(string input)
        {
            // Remove inline references like [1], [2], etc.
            string withoutInlineRefs = Regex.Replace(input, @"\[\d+\]", "");

            // Remove reference list at the end (lines starting with [number]:)
            string withoutRefList = Regex.Replace(withoutInlineRefs, @"\n\[\d+\]:.*(\n|$)", "");

            return withoutRefList.Trim();
        }

        public Uri GetCallbackUri()
        {
            return new Uri(_baseUri + $"/api/calls/{Guid.NewGuid()}");
        }

        public Uri GetTranscriptionTransportUri()
        {
            return new Uri($"wss://{_baseWssUri}/ws");
        }

        public void CleanupCall(string correlationId)
        {
            if (_cancellationTokenSources.TryRemove(correlationId, out var tokenSource))
            {
                tokenSource.Cancel();
                tokenSource.Dispose();
            }
            
            _ = _callStore.TryRemove(correlationId, out _);
            _logger.LogInformation("Cleaned up resources for call with correlation ID {CorrelationId}", correlationId);
        }

        public void RegisterTokenSource(string correlationId, CancellationTokenSource tokenSource)
        {
            _cancellationTokenSources[correlationId] = tokenSource;
        }

        private async Task<string> GetDirectLineTokenAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var httpClient = _httpClientFactory.CreateClient("DirectLine");
                
                // Clear the authorization header since we'll be using it in the URL
                var originalAuth = httpClient.DefaultRequestHeaders.Authorization;
                httpClient.DefaultRequestHeaders.Authorization = null;
                
                var content = new StringContent("{}", Encoding.UTF8, "application/json");
                
                // Different endpoint for token generation
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

        private class DirectLineTokenResponse
        {
            public string? Token { get; set; }
            public int ExpiresIn { get; set; }
        }
    }
}
