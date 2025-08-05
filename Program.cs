using Azure.Communication.CallAutomation;
using Azure.Identity;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ACSforMCS;
using ACSforMCS.Configuration;
using ACSforMCS.Services;
using ACSforMCS.Middleware;
using ACSforMCS.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console; // Added for ConsoleLoggerProvider
using Polly;
using Polly.Extensions.Http;

var builder = WebApplication.CreateBuilder(args);

// Configure Azure Key Vault
string keyVaultEndpoint = builder.Configuration["KeyVault:Endpoint"];
if (!string.IsNullOrEmpty(keyVaultEndpoint))
{
    // Add Azure Key Vault configuration provider
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultEndpoint),
        new DefaultAzureCredential());
    
    // Log that Key Vault configuration is being used
    Console.WriteLine("Azure Key Vault configuration provider added");
}
else
{
    Console.WriteLine("WARNING: Key Vault endpoint not configured, using local configuration values");
}

// Register configuration
builder.Services.Configure<AppSettings>(builder.Configuration);
builder.Services.Configure<VoiceOptions>(builder.Configuration.GetSection("Voice"));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Get settings from configuration
var appSettings = new AppSettings();
builder.Configuration.Bind(appSettings);

// Validate critical settings
ArgumentNullException.ThrowIfNullOrEmpty(appSettings.AcsConnectionString, nameof(appSettings.AcsConnectionString));
ArgumentNullException.ThrowIfNullOrEmpty(appSettings.CognitiveServiceEndpoint, nameof(appSettings.CognitiveServiceEndpoint));
ArgumentNullException.ThrowIfNullOrEmpty(appSettings.DirectLineSecret, nameof(appSettings.DirectLineSecret));

// Handle base URI from environment variable or config
var baseUri = Environment.GetEnvironmentVariable("VS_TUNNEL_URL")?.TrimEnd('/');
if (string.IsNullOrEmpty(baseUri))
{
    baseUri = appSettings.BaseUri?.TrimEnd('/');
    ArgumentNullException.ThrowIfNullOrEmpty(baseUri, nameof(appSettings.BaseUri));
}
appSettings.BaseUri = baseUri;

// Register dependencies
builder.Services.AddSingleton(new CallAutomationClient(connectionString: appSettings.AcsConnectionString));
builder.Services.AddSingleton<ConcurrentDictionary<string, CallContext>>(new ConcurrentDictionary<string, CallContext>());

// Set up HttpClient with Polly retry policies
builder.Services.AddHttpClient("DirectLine", client => {
    client.BaseAddress = new Uri(Constants.DirectLineBaseUrl);
    
    // Ensure proper authorization header format
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", appSettings.DirectLineSecret.Trim());
    
    // Add additional headers that might be required
    client.DefaultRequestHeaders.Add("User-Agent", "ACSforMCS/1.0");
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    
    // Set a reasonable timeout
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddTransientHttpErrorPolicy(policy => 
    policy.WaitAndRetryAsync(3, retryAttempt => 
        TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

// Register services
builder.Services.AddSingleton<CallAutomationService>();

// Add health checks
builder.Services.AddHealthChecks()
    .AddCheck<DirectLineHealthCheck>("directline_api", tags: new[] { "ready" });

// Add filters for logging - simpler approach
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.DirectLine.LogicalHandler", LogLevel.Warning);

var app = builder.Build();

// Register services for direct access
var callAutomationService = app.Services.GetRequiredService<CallAutomationService>();
var callStore = app.Services.GetRequiredService<ConcurrentDictionary<string, CallContext>>();
var client = app.Services.GetRequiredService<CallAutomationClient>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

app.MapGet("/", () => "Hello Azure Communication Services, here is Copilot Studio!");

app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation("Incoming Call event received : {EventGridEvent}", JsonConvert.SerializeObject(eventGridEvent));

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
        
        try
        {
            // Fixed: Add null checking for JSON parsing
            var jsonNode = JsonNode.Parse(eventGridEvent.Data);
            if (jsonNode == null)
            {
                logger.LogError("Failed to parse event data as JSON");
                continue;
            }

            var jsonObject = jsonNode.AsObject();
            var incomingCallContext = (string?)jsonObject["incomingCallContext"];

            if (string.IsNullOrEmpty(incomingCallContext))
            {
                logger.LogError("Missing incomingCallContext in event data");
                continue;
            }

            var callbackUri = callAutomationService.GetCallbackUri();
            
            var answerCallOptions = new AnswerCallOptions(incomingCallContext, callbackUri)
            {
                CallIntelligenceOptions = new CallIntelligenceOptions()
                {
                    CognitiveServicesEndpoint = new Uri(appSettings.CognitiveServiceEndpoint)
                },
                TranscriptionOptions = new TranscriptionOptions("en-US")
                {
                    TransportUri = callAutomationService.GetTranscriptionTransportUri(),
                    TranscriptionTransport = StreamingTransport.Websocket,
                    EnableIntermediateResults = true,
                    StartTranscription = true
                }
            };

            AnswerCallResult answerCallResult = await client.AnswerCallAsync(answerCallOptions);

            var correlationId = answerCallResult?.CallConnectionProperties.CorrelationId;
            logger.LogInformation("Correlation Id: {CorrelationId}", correlationId);

            if (correlationId != null)
            {
                callStore[correlationId] = new CallContext()
                {
                    CorrelationId = correlationId
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Answer call exception: {Message}", ex.Message);
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
        logger.LogInformation("Event received: {CloudEvent}", JsonConvert.SerializeObject(@event));

        var callConnection = client.GetCallConnection(@event.CallConnectionId);
        var callMedia = callConnection?.GetCallMedia();
        var correlationId = @event.CorrelationId;

        if (callConnection == null || callMedia == null)
        {
            return Results.BadRequest($"Call objects failed to get for connection id {@event.CallConnectionId}.");
        }

        if (@event is CallConnected)
        {
            try
            {
                Conversation? conversation = null;
                
                try
                {
                    // Try the regular method first
                    conversation = await callAutomationService.StartConversationAsync();
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("403"))
                {
                    // If we get a 403, try the alternative method with token
                    logger.LogWarning("Regular StartConversationAsync failed with 403, trying token method");
                    conversation = await callAutomationService.StartConversationWithTokenAsync();
                }
                
                if (conversation == null || string.IsNullOrEmpty(conversation.ConversationId))
                {
                    throw new InvalidOperationException("Failed to get valid conversation");
                }
                
                var conversationId = conversation.ConversationId;
                if (callStore.ContainsKey(correlationId))
                {
                    callStore[correlationId].ConversationId = conversationId;
                }

                // Start listening for Agent responses asynchronously
                var cts = new CancellationTokenSource();
                callAutomationService.RegisterTokenSource(correlationId, cts);
                
                // Fixed: Add null check for StreamUrl
                if (string.IsNullOrEmpty(conversation.StreamUrl))
                {
                    logger.LogError("StreamUrl is null or empty, cannot listen to bot");
                }
                else
                {
                    _ = Task.Run(() => callAutomationService.ListenToBotWebSocketAsync(
                        conversation.StreamUrl, callConnection, cts.Token));
                }

                await callAutomationService.SendMessageAsync(conversationId, "Hi");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing CallConnected event: {Message}", ex.Message);
            }
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
            logger.LogInformation("Transcription started: {OperationContext}", transcriptionStarted.OperationContext);
        }

        if (@event is TranscriptionStopped transcriptionStopped)
        {
            logger.LogInformation("Transcription stopped: {OperationContext}", transcriptionStopped.OperationContext);
        }
        
        if (@event is CallDisconnected)
        {
            logger.LogInformation("Call Disconnected");
            callAutomationService.CleanupCall(correlationId);
        }
    }
    return Results.Ok();
}).Produces(StatusCodes.Status200OK);

// Setup web socket handling
app.UseWebSockets();
app.UseCallWebSockets(); // Use our custom middleware

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();

// Add health check endpoint
app.MapHealthChecks("/health");

app.Run();
