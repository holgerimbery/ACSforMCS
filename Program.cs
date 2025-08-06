using Azure.Communication.CallAutomation;
using Azure.Communication;
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
using Microsoft.Extensions.Logging.Console;
using Polly;
using Polly.Extensions.Http;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

#region Azure Key Vault Configuration

// Configure Azure Key Vault for secure configuration management
// This allows storing sensitive values like connection strings and secrets in Azure Key Vault
// instead of in configuration files or environment variables
string keyVaultEndpoint = builder.Configuration["KeyVault:Endpoint"];
if (!string.IsNullOrEmpty(keyVaultEndpoint))
{
    // Add Azure Key Vault as a configuration provider using Managed Identity or DefaultAzureCredential
    // This automatically retrieves secrets from Key Vault and makes them available as configuration values
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultEndpoint),
        new DefaultAzureCredential());
    
    Console.WriteLine("Azure Key Vault configuration provider added");
}
else
{
    Console.WriteLine("WARNING: Key Vault endpoint not configured, using local configuration values");
}

#endregion

#region Configuration Setup

// Register configuration objects for dependency injection
// This binds JSON configuration sections to strongly-typed classes
builder.Services.Configure<AppSettings>(builder.Configuration);
builder.Services.Configure<VoiceOptions>(builder.Configuration.GetSection("Voice"));

// Add standard ASP.NET Core services
builder.Services.AddControllers(); // Enable MVC controllers for API endpoints
builder.Services.AddEndpointsApiExplorer(); // Enable API exploration for minimal APIs
builder.Services.AddSwaggerGen(); // Add Swagger/OpenAPI documentation generation

// Load and validate application settings
var appSettings = new AppSettings();
builder.Configuration.Bind(appSettings); // Bind configuration to the settings object

// Validate that critical configuration values are present
// These are required for the application to function properly
ArgumentNullException.ThrowIfNullOrEmpty(appSettings.AcsConnectionString, nameof(appSettings.AcsConnectionString));
ArgumentNullException.ThrowIfNullOrEmpty(appSettings.CognitiveServiceEndpoint, nameof(appSettings.CognitiveServiceEndpoint));
ArgumentNullException.ThrowIfNullOrEmpty(appSettings.DirectLineSecret, nameof(appSettings.DirectLineSecret));

#endregion

#region Base URI Configuration

// Handle base URI configuration with support for development tunneling and environment-specific values
// This supports both local development (with VS tunnels) and production deployment scenarios
var baseUri = Environment.GetEnvironmentVariable("VS_TUNNEL_URL")?.TrimEnd('/');
if (string.IsNullOrEmpty(baseUri))
{
    // Get environment-specific BaseUri from Key Vault
    // This allows different URLs for Development vs Production environments
    var environment = builder.Environment.EnvironmentName; // Gets "Development" or "Production"
    var secretName = $"BaseUri-{environment}";
    
    try
    {
        // Attempt to retrieve the environment-specific BaseUri from Azure Key Vault
        var keyVaultUri = new Uri(builder.Configuration["KeyVault:VaultUri"]);
        var secretClient = new SecretClient(keyVaultUri, new DefaultAzureCredential());
        var secret = secretClient.GetSecret(secretName);
        baseUri = secret.Value.Value?.TrimEnd('/');
        Console.WriteLine($"Loaded BaseUri from Key Vault secret '{secretName}'");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Failed to load {secretName} from Key Vault: {ex.Message}");
        // Fall back to the general BaseUri configuration if environment-specific one is not available
        baseUri = appSettings.BaseUri?.TrimEnd('/');
    }
    
    ArgumentNullException.ThrowIfNullOrEmpty(baseUri, "BaseUri not found in Key Vault or configuration");
}
appSettings.BaseUri = baseUri;

#endregion

#region Dependency Registration

// Register Azure Communication Services client as a singleton
// This client is used for all call automation operations (answering, transferring, etc.)
builder.Services.AddSingleton(new CallAutomationClient(connectionString: appSettings.AcsConnectionString));

// Register thread-safe call context storage for managing active calls
// This dictionary maps correlation IDs to call contexts, enabling proper routing of events and messages
builder.Services.AddSingleton<ConcurrentDictionary<string, CallContext>>(new ConcurrentDictionary<string, CallContext>());

// Configure HTTP client for DirectLine API communication with retry policies
builder.Services.AddHttpClient("DirectLine", client => {
    // Set the base address for all DirectLine API calls
    client.BaseAddress = new Uri(Constants.DirectLineBaseUrl);
    
    // Configure authentication using the DirectLine secret as a Bearer token
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", appSettings.DirectLineSecret.Trim());
    
    // Add standard headers required by DirectLine API
    client.DefaultRequestHeaders.Add("User-Agent", "ACSforMCS/1.0");
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    
    // Set a reasonable timeout for API calls
    client.Timeout = TimeSpan.FromSeconds(30);
})
// Add Polly retry policy for handling transient HTTP errors
// This implements exponential backoff with 3 retry attempts for failed requests
.AddTransientHttpErrorPolicy(policy => 
    policy.WaitAndRetryAsync(3, retryAttempt => 
        TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

// Register the main call automation service that orchestrates bot communication
builder.Services.AddSingleton<CallAutomationService>();

#endregion

#region Health Checks

// Add health check monitoring for external dependencies
// This enables monitoring of system health and readiness for handling calls
builder.Services.AddHealthChecks()
    .AddCheck<DirectLineHealthCheck>("directline_api", tags: new[] { "ready" });

#endregion

#region Logging Configuration

// Configure logging to reduce noise from HTTP client and hosting diagnostics
// This focuses logging on application-specific events while reducing system noise
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.DirectLine.LogicalHandler", LogLevel.Warning);

#endregion

var app = builder.Build();

#region Service Resolution

// Resolve services for direct access in API endpoints
// These are used throughout the endpoint handlers for call processing
var callAutomationService = app.Services.GetRequiredService<CallAutomationService>();
var callStore = app.Services.GetRequiredService<ConcurrentDictionary<string, CallContext>>();
var client = app.Services.GetRequiredService<CallAutomationClient>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

#endregion

#region API Endpoints

/// <summary>
/// Basic health check endpoint that confirms the service is running.
/// </summary>
app.MapGet("/", () => "Hello Azure Communication Services, here is Copilot Studio!");

/// <summary>
/// Webhook endpoint for handling incoming call events from Azure Communication Services.
/// This endpoint receives EventGrid events when new calls arrive and initiates the call handling process.
/// </summary>
app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation("Incoming Call event received : {EventGridEvent}", JsonConvert.SerializeObject(eventGridEvent));

        // Handle EventGrid system events (like subscription validation)
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event required by EventGrid
            // This is sent when setting up the webhook subscription
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
            // Parse the incoming call event data
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

            // Generate a unique callback URI for this call's events
            var callbackUri = callAutomationService.GetCallbackUri();
            
            // Configure call answering with AI capabilities
            var answerCallOptions = new AnswerCallOptions(incomingCallContext, callbackUri)
            {
                // Enable AI-powered call intelligence features
                CallIntelligenceOptions = new CallIntelligenceOptions()
                {
                    CognitiveServicesEndpoint = new Uri(appSettings.CognitiveServiceEndpoint)
                },
                // Configure real-time speech-to-text transcription
                TranscriptionOptions = new TranscriptionOptions("en-US")
                {
                    TransportUri = callAutomationService.GetTranscriptionTransportUri(),
                    TranscriptionTransport = StreamingTransport.Websocket, // Use WebSocket for real-time streaming
                    EnableIntermediateResults = true, // Get partial results for better responsiveness
                    StartTranscription = true // Begin transcription immediately when call connects
                }
            };

            // Answer the incoming call with the configured options
            AnswerCallResult answerCallResult = await client.AnswerCallAsync(answerCallOptions);

            // Store the call context for later reference
            var correlationId = answerCallResult?.CallConnectionProperties.CorrelationId;
            logger.LogInformation("Correlation Id: {CorrelationId}", correlationId);

            if (correlationId != null)
            {
                // Create and store call context for tracking this call's state
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

/// <summary>
/// Webhook endpoint for handling call automation events from Azure Communication Services.
/// This endpoint receives events throughout the call lifecycle (connected, disconnected, transfer events, etc.)
/// and coordinates the bot conversation flow.
/// </summary>
app.MapPost("/api/calls/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    ILogger<Program> logger) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        // Parse the call automation event
        CallAutomationEventBase @event = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation("Event received: {CloudEvent}", JsonConvert.SerializeObject(@event));

        // Get call connection and media objects for this event
        var callConnection = client.GetCallConnection(@event.CallConnectionId);
        var callMedia = callConnection?.GetCallMedia();
        var correlationId = @event.CorrelationId;

        if (callConnection == null || callMedia == null)
        {
            return Results.BadRequest($"Call objects failed to get for connection id {@event.CallConnectionId}.");
        }

        // Handle call connected event - this is when we start the bot conversation
        if (@event is CallConnected callConnected)
        {
            try
            {
                Conversation? conversation = null;
                
                try
                {
                    // Attempt to start a DirectLine conversation with the bot
                    conversation = await callAutomationService.StartConversationAsync();
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("403"))
                {
                    // If the primary method fails with authentication issues, try the token-based approach
                    logger.LogWarning("Regular StartConversationAsync failed with 403, trying token method");
                    conversation = await callAutomationService.StartConversationWithTokenAsync();
                }
                
                if (conversation == null || string.IsNullOrEmpty(conversation.ConversationId))
                {
                    throw new InvalidOperationException("Failed to get valid conversation");
                }
                
                // Associate the bot conversation with this call
                var conversationId = conversation.ConversationId;
                if (callStore.ContainsKey(correlationId))
                {
                    callStore[correlationId].ConversationId = conversationId;
                }

                // Start listening for bot responses asynchronously
                var cts = new CancellationTokenSource();
                callAutomationService.RegisterTokenSource(correlationId, cts);
                
                // Validate that we have a WebSocket URL for real-time bot communication
                if (string.IsNullOrEmpty(conversation.StreamUrl))
                {
                    logger.LogError("StreamUrl is null or empty, cannot listen to bot");
                }
                else
                {
                    // Start the bot listener in a background task
                    _ = Task.Run(() => callAutomationService.ListenToBotWebSocketAsync(
                        conversation.StreamUrl, callConnection, cts.Token));
                }

                // Send initial greeting message to the bot to start the conversation
                await callAutomationService.SendMessageAsync(conversationId, "Hi");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing CallConnected event: {Message}", ex.Message);
            }
        }

        // Handle call transfer events for monitoring and logging
        if (@event is CallTransferAccepted transferAccepted)
        {
            logger.LogInformation("Call transfer accepted: {OperationContext}", transferAccepted.OperationContext);
        }

        if (@event is CallTransferFailed transferFailed)
        {
            logger.LogError("Call transfer failed: {OperationContext}, Code: {ResultCode}", 
                transferFailed.OperationContext, transferFailed.ResultInformation?.Code);
        }

        // Handle audio playback events for monitoring TTS operations
        if (@event is PlayFailed)
        {
            logger.LogInformation("Play Failed");
        }

        if (@event is PlayCompleted)
        {
            logger.LogInformation("Play Completed");            
        }

        // Handle transcription lifecycle events
        if (@event is TranscriptionStarted transcriptionStarted)
        {
            logger.LogInformation("Transcription started: {OperationContext}", transcriptionStarted.OperationContext);
        }

        if (@event is TranscriptionStopped transcriptionStopped)
        {
            logger.LogInformation("Transcription stopped: {OperationContext}", transcriptionStopped.OperationContext);
        }
        
        // Handle call disconnection - clean up resources
        if (@event is CallDisconnected)
        {
            logger.LogInformation("Call Disconnected");
            // Clean up call-specific resources and cancel ongoing operations
            callAutomationService.CleanupCall(correlationId);
        }
    }
    return Results.Ok();
}).Produces(StatusCodes.Status200OK);

#endregion

#region Middleware Configuration

// Enable WebSocket support for real-time audio streaming
app.UseWebSockets();
// Add custom middleware for handling WebSocket connections from ACS
app.UseCallWebSockets(); // Use our custom middleware for call-specific WebSocket handling

#endregion

#region Pipeline Configuration

// Configure the HTTP request pipeline based on environment
if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    // Enable Swagger UI for API documentation and testing
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Enable authorization middleware (though not heavily used in this application)
app.UseAuthorization();

// Map controller routes for any additional API controllers
app.MapControllers();

// Add health check endpoint for monitoring system health
// This can be used by load balancers and monitoring systems to check service status
app.MapHealthChecks("/health");

#endregion

// Start the application and begin listening for requests
app.Run();
