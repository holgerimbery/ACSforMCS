using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JsonException = Newtonsoft.Json.JsonException;
using System.Net.Http.Headers;
using ACSforMCS;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

//Get ACS Connection String from appsettings.json
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString);

//Call Automation Client
var client = new CallAutomationClient(connectionString: acsConnectionString);

//Get the Cognitive Services endpoint from appsettings.json
var cognitiveServicesEndpoint = builder.Configuration.GetValue<string>("CognitiveServiceEndpoint");
ArgumentNullException.ThrowIfNullOrEmpty(cognitiveServicesEndpoint);

//Get Agent Phone number from appsettings.json
var agentPhonenumber = builder.Configuration.GetValue<string>("AgentPhoneNumber");
ArgumentNullException.ThrowIfNullOrEmpty(agentPhonenumber);

// Get Direct Line Secret from appsettings.json
var directLineSecret = builder.Configuration.GetValue<string>("DirectLineSecret");

ArgumentNullException.ThrowIfNullOrEmpty(directLineSecret);

// Create an HTTP client to communicate with the Direct Line service
HttpClient httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", directLineSecret);

var baseUri = Environment.GetEnvironmentVariable("VS_TUNNEL_URL")?.TrimEnd('/');
//var baseUri = builder.Configuration.GetValue<string>("BaseUri")?.TrimEnd('/');

if (string.IsNullOrEmpty(baseUri))
{
    baseUri = builder.Configuration.GetValue<string>("BaseUri")?.TrimEnd('/');
}
var baseWssUri = baseUri.Split("https://")[1];

ConcurrentDictionary<string, CallContext> CallStore = new();

var app = builder.Build();

app.MapGet("/", () => "Hello Azure Communication Services, here is Copilot Studio!");

app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation($"Incoming Call event received : {JsonConvert.SerializeObject(eventGridEvent)}");

        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }
        var jsonObject = JsonNode.Parse(eventGridEvent.Data).AsObject();
        var incomingCallContext = (string)jsonObject["incomingCallContext"];

        var callbackUri = new Uri(baseUri + $"/api/calls/{Guid.NewGuid()}");
        
        var answerCallOptions = new AnswerCallOptions(incomingCallContext, callbackUri)
        {
            CallIntelligenceOptions = new CallIntelligenceOptions()
            {
                CognitiveServicesEndpoint = new Uri(cognitiveServicesEndpoint)
            },
            TranscriptionOptions = new TranscriptionOptions("en-US")
            {
                TransportUri = new Uri($"wss://{baseWssUri}/ws"),
                TranscriptionTransport = StreamingTransport.Websocket,
                EnableIntermediateResults = true,
                StartTranscription = true
            }
        };

        try
        {
            AnswerCallResult answerCallResult = await client.AnswerCallAsync(answerCallOptions);

            var correlationId = answerCallResult?.CallConnectionProperties.CorrelationId;
            logger.LogInformation($"Correlation Id: {correlationId}");

            if (correlationId != null)
            {
                CallStore[correlationId] = new CallContext()
                {
                    CorrelationId = correlationId
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogError($"Answer call exception: {ex.Message}. Stack trace: {ex.StackTrace}");
        }
    }
    return Results.Ok();
});

app.MapPost("/api/calls/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    ILogger<Program> logger) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase @event = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation($"Event received: {JsonConvert.SerializeObject(@event)}");

        var callConnection = client.GetCallConnection(@event.CallConnectionId);
        var callMedia = callConnection?.GetCallMedia();
        var correlationId = @event.CorrelationId;

        if (callConnection == null || callMedia == null)
        {
            return Results.BadRequest($"Call objects failed to get for connection id {@event.CallConnectionId}.");
        }

        if (@event is CallConnected callConnected)
        {
            var conversation = await StartConversationAsync();
            var conversationId = conversation.ConversationId;
            if (CallStore.ContainsKey(correlationId))
            {
                CallStore[correlationId].ConversationId = conversationId;
            }

            // Start listening for Agent responses asynchronously
            var cts = new CancellationTokenSource();
            Task.Run(() => ListenToBotWebSocketAsync(conversation.StreamUrl, callConnection, cts.Token, logger));

            await SendMessageAsync(conversationId, "Hi");
        }

        if (@event is PlayFailed)
        {
            logger.LogInformation("Play Failed");
        }

        if (@event is PlayCompleted)
        {
            logger.LogInformation("Play Completed");            
        }

        if (@event is TranscriptionStarted transcriptionStarted)
        {
            logger.LogInformation($"Transcription started: {transcriptionStarted.OperationContext}");
        }

        if (@event is TranscriptionStopped transcriptionStopped)
        {
            logger.LogInformation($"Transcription stopped: {transcriptionStopped.OperationContext}");
        }
        
        if(@event is CallDisconnected callDisconnected)
        {
            logger.LogInformation("Call Disconnected");
            _ = CallStore.TryRemove(@event.CorrelationId, out CallContext context);
        }
    }
    return Results.Ok();
}).Produces(StatusCodes.Status200OK);

// setup web socket for stream in
app.UseWebSockets();
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws")
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        
        if (context.WebSockets.IsWebSocketRequest)
        {
            // Extract correlation ID and call connection ID
            var correlationId = context.Request.Headers["x-ms-call-correlation-id"].FirstOrDefault();
            var callConnectionId = context.Request.Headers["x-ms-call-connection-id"].FirstOrDefault();
            var callMedia = callConnectionId != null ? client.GetCallConnection(callConnectionId)?.GetCallMedia() : null;
            
            logger.LogInformation($"WebSocket connection established - Correlation ID: {correlationId}, Call Connection ID: {callConnectionId}");
            
            string conversationId = null;
            if (correlationId != null && CallStore.TryGetValue(correlationId, out var callContext))
            {
                conversationId = callContext.ConversationId;
            }
            else
            {
                logger.LogWarning($"No call context found for correlation ID: {correlationId}");
            }

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                string partialData = "";

                while (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseSent)
                {
                    byte[] receiveBuffer = new byte[4096];
                    var cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(1200)).Token;
                    WebSocketReceiveResult receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cancellationToken);

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
                                        logger.LogDebug("Intermediate transcription received, canceling prompt");
                                        if (callMedia != null)
                                            await callMedia.CancelAllMediaOperationsAsync();
                                    }
                                    else
                                    {
                                        var streamingData = StreamingData.Parse(data);
                                        if (streamingData is TranscriptionMetadata transcriptionMetadata)
                                        {
                                            callMedia = client.GetCallConnection(transcriptionMetadata.CallConnectionId)?.GetCallMedia();
                                        }
                                        if (streamingData is TranscriptionData transcriptionData)
                                        {
                                            logger.LogDebug($"Transcription data received: {transcriptionData.Text}");

                                            if (transcriptionData.ResultState == TranscriptionResultState.Final)
                                            {
                                                if (conversationId == null && correlationId != null && 
                                                    CallStore.TryGetValue(correlationId, out var ctx))
                                                {
                                                    conversationId = ctx.ConversationId;
                                                }

                                                if (!string.IsNullOrEmpty(conversationId))
                                                {
                                                    await SendMessageAsync(conversationId, transcriptionData.Text);
                                                    logger.LogInformation($"Message sent to conversation: {transcriptionData.Text}");
                                                }
                                                else
                                                {
                                                    logger.LogWarning("Conversation Id is null, unable to send message");
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
                            logger.LogError($"WebSocket data processing error: {ex.Message}. Stack trace: {ex.StackTrace}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"WebSocket connection error: {ex.Message}. Stack trace: {ex.StackTrace}");
            }
            finally
            {
                logger.LogInformation("WebSocket connection closed");
            }
        }
        else
        {
            logger.LogWarning("Non-WebSocket request received at WebSocket endpoint");
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
    else
    {
        await next(context);
    }
});


async Task<Conversation> StartConversationAsync()
{
    var response = await httpClient.PostAsync("https://directline.botframework.com/v3/directline/conversations", null);
    response.EnsureSuccessStatusCode();
    var content = await response.Content.ReadAsStringAsync();
    return JsonConvert.DeserializeObject<Conversation>(content);
}

async Task ListenToBotWebSocketAsync(string streamUrl, CallConnection callConnection, CancellationToken cancellationToken, ILogger logger)
{
    if (string.IsNullOrEmpty(streamUrl))
    {
        logger.LogWarning("WebSocket streaming is not enabled for this MCS Agent.");
        return;
    }

    using (var webSocket = new ClientWebSocket())
    {
        try
        {
            await webSocket.ConnectAsync(new Uri(streamUrl), cancellationToken);
            logger.LogInformation($"Connected to bot WebSocket at {streamUrl}");

            var buffer = new byte[4096]; // Set the buffer size to 4096 bytes
            var messageBuilder = new StringBuilder();

            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                messageBuilder.Clear(); // Reset buffer for each new message
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                } while (!result.EndOfMessage); // Continue until we've received the full message

                string rawMessage = messageBuilder.ToString();
                var AgentActivity = ExtractLatestAgentActivity(rawMessage, logger);

                if (AgentActivity.Type == "message")
                {
                    logger.LogInformation($"Playing Agent Response: {AgentActivity.Text}");
                    await PlayToAllAsync(callConnection.GetCallMedia(), AgentActivity.Text);
                }
                else if (AgentActivity.Type == "endOfConversation")
                {
                    logger.LogInformation("End of Conversation signal received, hanging up call");
                    await callConnection.HangUpAsync(true);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError($"Bot WebSocket error: {ex.Message}. Stack trace: {ex.StackTrace}");
        }
        finally
        {
            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                    logger.LogInformation("Bot WebSocket connection closed gracefully");
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Error during WebSocket closure: {ex.Message}");
                }
            }
        }
    }
}

async Task SendMessageAsync(string conversationId, string message)
{
    var messagePayload = new
    {
        type = "message",
        from = new { id = "user1" },
        text = message
    };
    string messageJson = JsonConvert.SerializeObject(messagePayload);
    StringContent content = new StringContent(messageJson, Encoding.UTF8, "application/json");

    var response = await httpClient.PostAsync($"https://directline.botframework.com/v3/directline/conversations/{conversationId}/activities", content);
    response.EnsureSuccessStatusCode();
}


static AgentActivity ExtractLatestAgentActivity(string rawMessage, ILogger logger = null)
{
    try
    {
        using var doc = JsonDocument.Parse(rawMessage);

        if (doc.RootElement.TryGetProperty("activities", out var activities) && activities.ValueKind == JsonValueKind.Array)
        {
            // Iterate in reverse order to get the latest message
            for (int i = activities.GetArrayLength() - 1; i >= 0; i--)
            {
                var activity = activities[i];

                if (activity.TryGetProperty("type", out var type))
                {
                    if (type.GetString() == "message")
                    {
                        if (activity.TryGetProperty("from", out var from) &&
                            from.TryGetProperty("id", out var fromId) &&
                            fromId.GetString() != "user1") // Ensure message is from Agent
                        {
                            if (activity.TryGetProperty("speak", out var speak))
                            {
                                logger?.LogDebug($"Voice content received: {speak.GetString()}");
                                return new AgentActivity()
                                {
                                    Type = "message",
                                    Text = RemoveReferences(speak.GetString())
                                };
                            }

                            if (activity.TryGetProperty("text", out var text))
                            {
                                return new AgentActivity()
                                {
                                    Type = "message",
                                    Text = RemoveReferences(text.GetString())
                                };
                            }
                        }
                    }
                    else if(type.GetString() == "endOfConversation")
                    {
                        logger?.LogInformation("EndOfConversation activity received");
                        return new AgentActivity()
                        {
                            Type = "endOfConversation"
                        };
                    }
                }
            }
        }
    }
    catch (JsonException ex)
    {
        logger?.LogWarning($"Unexpected JSON format in agent activity: {ex.Message}");
    }
    
    logger?.LogWarning("No valid agent activity found in message, returning default error response");
    return new AgentActivity()
    {
        Type = "Error",
        Text = "Sorry, Something went wrong"
    };
}

static string RemoveReferences(string input)
{
    // Remove inline references like [1], [2], etc.
    string withoutInlineRefs = Regex.Replace(input, @"\[\d+\]", "");

    // Remove reference list at the end (lines starting with [number]:)
    string withoutRefList = Regex.Replace(withoutInlineRefs, @"\n\[\d+\]:.*(\n|$)", "");

    return withoutRefList.Trim();
}

async Task PlayToAllAsync(CallMedia callConnectionMedia, string message)
 {
    var ssmlPlaySource = new SsmlSource($"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\"><voice name=\"en-US-NancyNeural\">{message}</voice></speak>");

    var playOptions = new PlayToAllOptions(ssmlPlaySource)
    {
        OperationContext = "Testing"
    };

    await callConnectionMedia.PlayToAllAsync(playOptions);
 }


// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();
app.Run();
