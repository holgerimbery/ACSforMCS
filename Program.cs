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
string? keyVaultEndpoint = builder.Configuration["KeyVault:Endpoint"];
if (!string.IsNullOrEmpty(keyVaultEndpoint))
{
    // Add Azure Key Vault as a configuration provider using Managed Identity or DefaultAzureCredential
    // This automatically retrieves secrets from Key Vault and makes them available as configuration values
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultEndpoint),
        new DefaultAzureCredential());
    
    // Key Vault configuration logged after app is built
}
else
{
    // Warning will be logged after app is built
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

// Add Swagger generation only in Development environment for security
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "ACS for MCS API",
            Version = "v1.0",
            Description = @"
# Azure Communication Services for Microsoft Copilot Studio (Development)

A comprehensive telephony automation solution that integrates Azure Communication Services with Microsoft Copilot Studio.

## ⚠️ DEVELOPMENT ENVIRONMENT
This API documentation is only available in Development environment. Production deployments have Swagger disabled for security.

## Features
- **Incoming Call Handling**: Automatically answers phone calls and connects them to your Copilot Studio agent
- **Real-time Transcription**: Speech-to-text conversion using Azure Cognitive Services
- **Bot Integration**: Seamless communication with Microsoft Copilot Studio via DirectLine
- **Call Monitoring**: Comprehensive health checks and monitoring endpoints
- **Scalable Architecture**: Built for enterprise-grade telephony automation

## Security
- **Development & Production**: All monitoring endpoints require `X-API-Key` header authentication
- **Azure Key Vault**: Secure configuration management with environment-specific secrets
- **Managed Identity**: Secure Azure resource access
- **API Documentation**: Requires authentication even in Development environment

## Authentication Required
All secured endpoints require the `X-API-Key` header with the value from Azure Key Vault secret `HealthCheckApiKey`.

## Architecture
```
Phone Call → Azure Communication Services → Event Grid → ACS for MCS → DirectLine → Copilot Studio
```
",
            Contact = new Microsoft.OpenApi.Models.OpenApiContact
            {
                Name = "ACS for MCS Project",
                Url = new Uri("https://github.com/holgerimbery/ACSforMCS"),
                Email = "holger@imbery.de"
            },
            License = new Microsoft.OpenApi.Models.OpenApiLicense
            {
                Name = "MIT License",
                Url = new Uri("https://github.com/holgerimbery/ACSforMCS/blob/main/LICENSE.md")
            }
        });

        // Configure API Key authentication for Swagger
        c.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Name = "X-API-Key",
            Description = @"
**API Key Authentication Required**

All monitoring and health check endpoints require authentication via the `X-API-Key` header in both Development and Production environments.

**Usage:**
```
X-API-Key: your-health-check-api-key
```

**Note:** The API key is stored in Azure Key Vault as `HealthCheckApiKey` secret and is required for accessing this documentation.
",
            Scheme = "ApiKeyScheme"
        });

        // Tag definitions for better organization
        c.TagActionsBy(api => api.ActionDescriptor.RouteValues.ContainsKey("action") 
            ? new[] { api.ActionDescriptor.RouteValues["action"] ?? "Default" }
            : new[] { api.GroupName ?? "Default" });
        
        // Custom schema IDs to avoid conflicts
        c.CustomSchemaIds(type => type.FullName?.Replace("+", "."));

        // Add server information for Development
        c.AddServer(new Microsoft.OpenApi.Models.OpenApiServer
        {
            Url = "https://acsformcs.azurewebsites.net",
            Description = "Azure Web App (Development Mode)"
        });
        
        c.AddServer(new Microsoft.OpenApi.Models.OpenApiServer
        {
            Url = "https://localhost:5252",
            Description = "Local Development Server"
        });

        // Include XML comments if available
        var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (File.Exists(xmlPath))
        {
            c.IncludeXmlComments(xmlPath);
        }

        // Add global security requirement for secured endpoints
        c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        {
            {
                new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Reference = new Microsoft.OpenApi.Models.OpenApiReference
                    {
                        Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                        Id = "ApiKey"
                    }
                },
                Array.Empty<string>()
            }
        });
    }); // Add Swagger/OpenAPI documentation generation for Development only
}

// Load and validate application settings
var appSettings = new AppSettings();
builder.Configuration.Bind(appSettings); // Bind configuration to the settings object

// Validate that critical configuration values are present
// These are required for the application to function properly
ArgumentNullException.ThrowIfNullOrEmpty(appSettings.AcsConnectionString, nameof(appSettings.AcsConnectionString));
ArgumentNullException.ThrowIfNullOrEmpty(appSettings.CognitiveServiceEndpoint, nameof(appSettings.CognitiveServiceEndpoint));
ArgumentNullException.ThrowIfNullOrEmpty(appSettings.DirectLineSecret, nameof(appSettings.DirectLineSecret));

#endregion

# region Base URI Configuration

// Handle base URI configuration with clear precedence and proper error handling
// This supports both local development (with VS tunnels) and production deployment scenarios
var baseUri = Environment.GetEnvironmentVariable("VS_TUNNEL_URL")?.TrimEnd('/');
if (string.IsNullOrEmpty(baseUri))
{
    // Get environment-specific BaseUri from Key Vault (ONLY source for deployed environments)
    // This ensures each environment has its own dedicated configuration
    var environment = builder.Environment.EnvironmentName; // Gets "Development" or "Production"
    var secretName = $"BaseUri-{environment}";
    
    baseUri = await GetBaseUriFromKeyVaultAsync(builder.Configuration, secretName);
    
    // No fallback to avoid configuration conflicts like DevTunnel URLs persisting in general secrets
    // Each environment MUST have its own BaseUri-{Environment} secret in Key Vault
    if (string.IsNullOrEmpty(baseUri))
    {
        throw new InvalidOperationException($"BaseUri configuration missing. Required Key Vault secret '{secretName}' not found or empty. " +
            $"Environment: {environment}. Please ensure the secret exists and contains the correct URL for this environment.");
    }
}

// Log the source and value for debugging (without exposing the full URL in logs)
var source = Environment.GetEnvironmentVariable("VS_TUNNEL_URL") != null ? "VS_TUNNEL_URL environment variable" : $"Key Vault secret 'BaseUri-{builder.Environment.EnvironmentName}'";
Console.WriteLine($"✅ BaseUri loaded from: {source}");

appSettings.BaseUri = baseUri;

// Update the AppSettings configuration in the DI container to include the BaseUri
// This ensures that injected AppSettings instances have the correct BaseUri value
builder.Services.Configure<AppSettings>(settings => settings.BaseUri = baseUri);

#endregion

// Load Health Check API Key from Key Vault for production security
string? healthCheckApiKey = null;

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
    
    // Set an optimized timeout for API calls - reduced for faster failure detection
    client.Timeout = TimeSpan.FromSeconds(10);
    
    // NEW: Configure connection pooling for better performance
    client.DefaultRequestHeaders.ConnectionClose = false;
})
// NEW: Configure connection pooling and performance settings
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
{
    MaxConnectionsPerServer = 10, // Limit concurrent connections per server
    UseCookies = false, // Disable cookies for better performance in API scenarios
    UseProxy = false // Skip proxy detection for better performance
})
// Enhanced Polly retry policy with better logging and jitter
.AddTransientHttpErrorPolicy(policy => 
    policy.WaitAndRetryAsync(
        retryCount: 3,
        sleepDurationProvider: retryAttempt => 
        {
            // Add jitter to prevent thundering herd
            var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
            return baseDelay.Add(jitter);
        },
        onRetry: (outcome, timespan, retryCount, context) =>
        {
            // Note: Retry logging will be improved to use proper ILogger in future update
                // Note: Retry logging will be improved to use proper ILogger in future update
            Console.WriteLine($"DirectLine retry attempt {retryCount} after {timespan.TotalMilliseconds}ms due to: {outcome.Exception?.Message ?? "HTTP error"}");
        }));

// NEW: Add dedicated HTTP client for health checks
builder.Services.AddHttpClient("DirectLineHealth", client => {
    client.BaseAddress = new Uri("https://europe.directline.botframework.com/");
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.Timeout = TimeSpan.FromSeconds(10); // Shorter timeout for health checks
    client.DefaultRequestHeaders.ConnectionClose = false;
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
{
    MaxConnectionsPerServer = 5,
    UseCookies = false,
    UseProxy = false
});

// Register the main call automation service that orchestrates bot communication
builder.Services.AddSingleton<CallAutomationService>();

// Add memory cache service for SSML template caching and performance optimization
builder.Services.AddMemoryCache();

#endregion

#region Health Checks

// Add health check monitoring only in Development for debugging
// Production uses external monitoring tools (Application Insights, Azure Monitor) for better performance
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddHealthChecks()
        .AddCheck<DirectLineHealthCheck>("directline_api", tags: new[] { "ready" });
}

#endregion

#region Logging Configuration

// Configure logging based on environment for optimal performance
if (builder.Environment.IsDevelopment())
{
    // Development: Detailed logging for debugging
    builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Information);
    builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
    builder.Logging.AddFilter("System.Net.Http.HttpClient.DirectLine.LogicalHandler", LogLevel.Information);
    // Reduce WebSocket message processing noise
    builder.Logging.AddFilter("ACSforMCS.Services.CallAutomationService", LogLevel.Information);
    builder.Logging.AddFilter("ACSforMCS.Middleware.WebSocketMiddleware", LogLevel.Information);
}
else
{
    // Production: Minimal logging for maximum performance
    // Only log warnings and errors to reduce I/O overhead and improve performance
    builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Error);
    builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Error);
    builder.Logging.AddFilter("System.Net.Http.HttpClient.DirectLine.LogicalHandler", LogLevel.Error);
    builder.Logging.AddFilter("ACSforMCS.Services.CallAutomationService", LogLevel.Warning);
    builder.Logging.AddFilter("ACSforMCS.Middleware.WebSocketMiddleware", LogLevel.Warning);
    
    // Set global minimum log level for production performance
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
}

#endregion

var app = builder.Build();

// Load Health Check API Key only for Development environment
// Production has monitoring disabled for maximum performance
if (app.Environment.IsDevelopment())
{
    healthCheckApiKey = await GetSecretFromKeyVaultAsync(builder.Configuration, "HealthCheckApiKey");
    if (string.IsNullOrEmpty(healthCheckApiKey))
    {
        Console.WriteLine($"Warning: HealthCheckApiKey not found in Key Vault for Development environment - health endpoints will be disabled");
    }
}
else
{
    // Production: No health check API key needed since monitoring is disabled
    Console.WriteLine("Production mode: Health monitoring disabled for optimal performance");
}

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
/// Returns a simple greeting message to verify the service is operational.
/// </summary>
/// <returns>A greeting message indicating the service is running</returns>
/// <response code="200">Service is running normally</response>
app.MapGet("/", () => "Hello Azure Communication Services, here is Copilot Studio!")
    .WithName("GetServiceStatus")
    .WithTags("Service")
    .WithSummary("Service Status Check")
    .WithDescription("Returns a greeting message to confirm the service is running")
    .Produces<string>(StatusCodes.Status200OK);

/// <summary>
/// Webhook endpoint for handling incoming call events from Azure Communication Services.
/// This endpoint receives EventGrid events when new calls arrive and initiates the call handling process.
/// </summary>
/// <param name="eventGridEvents">Array of EventGrid events containing call information</param>
/// <param name="logger">Logger instance for request logging</param>
/// <returns>200 OK with subscription validation response if needed</returns>
/// <response code="200">Event processed successfully</response>
/// <response code="400">Invalid event data</response>
app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    logger.LogInformation("IncomingCall webhook received with {EventCount} events", eventGridEvents.Length);
    
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation("Processing event: Type={EventType}, Subject={Subject}", 
            eventGridEvent.EventType, eventGridEvent.Subject);
        logger.LogInformation("Incoming Call event received : {EventGridEvent}", JsonConvert.SerializeObject(eventGridEvent));

        // Handle EventGrid system events (like subscription validation)
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            logger.LogInformation("System event detected: {EventDataType}", eventData.GetType().Name);
            // Handle the subscription validation event required by EventGrid
            // This is sent when setting up the webhook subscription
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                logger.LogInformation("Subscription validation event - returning validation code");
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }
        
        try
        {
            logger.LogInformation("Parsing incoming call event data");
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

            logger.LogInformation("Processing call with context: {IncomingCallContext}", incomingCallContext);

            // Generate a unique callback URI for this call's events
            var callbackUri = callAutomationService.GetCallbackUri();
            logger.LogInformation("Generated callback URI: {CallbackUri}", callbackUri);
            
            // Load voice options for optimized speech recognition
            var voiceOptions = app.Services.GetRequiredService<IOptions<VoiceOptions>>().Value;
            
            // Configure call answering with AI capabilities and optimized speech recognition
            var answerCallOptions = new AnswerCallOptions(incomingCallContext, callbackUri)
            {
                // Enable AI-powered call intelligence features
                CallIntelligenceOptions = new CallIntelligenceOptions()
                {
                    CognitiveServicesEndpoint = new Uri(appSettings.CognitiveServiceEndpoint)
                },
                // Configure real-time speech-to-text transcription with optimization
                TranscriptionOptions = new TranscriptionOptions(voiceOptions.Language)
                {
                    TransportUri = callAutomationService.GetTranscriptionTransportUri(),
                    TranscriptionTransport = StreamingTransport.Websocket, // Use WebSocket for real-time streaming
                    EnableIntermediateResults = voiceOptions.EnableFastMode, // Enable for faster response in fast mode
                    StartTranscription = true // Begin transcription immediately when call connects
                }
            };

            logger.LogInformation("Answering call with ACS client");
            // Answer the incoming call with the configured options
            AnswerCallResult answerCallResult = await client.AnswerCallAsync(answerCallOptions);
            logger.LogInformation("Call answered successfully");

            // Store the call context for later reference
            var correlationId = answerCallResult?.CallConnectionProperties.CorrelationId;
            logger.LogInformation("Call answered - Correlation Id: {CorrelationId}", correlationId);

            if (correlationId != null)
            {
                // Create and store call context for tracking this call's state
                callStore[correlationId] = new CallContext()
                {
                    CorrelationId = correlationId
                };
                
                // Log concurrent call metrics
                logger.LogInformation("Call context created - Total active calls: {ActiveCalls}, Correlation ID: {CorrelationId}",
                    callStore.Count, correlationId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Answer call exception - Message: {ErrorMessage}, StackTrace: {StackTrace}", 
                ex.Message, ex.StackTrace);
        }
    }
    
    logger.LogInformation("IncomingCall webhook processing completed");
    return Results.Ok();
})
.WithName("HandleIncomingCall")
.WithTags("Call Automation")
.WithSummary("Handle Incoming Call Events")
.WithDescription("Webhook endpoint for Azure Communication Services EventGrid events. Handles incoming call notifications and initiates bot conversations.")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest);

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
                // Log concurrent call monitoring information
                logger.LogInformation("📞 Call connected - Active calls: {ActiveCallCount}, New call correlation ID: {CorrelationId}", 
                    callStore.Count, correlationId);
                
                Conversation? conversation = null;
                
                try
                {
                    logger.LogInformation("🤖 Starting DirectLine conversation for call {CorrelationId}", correlationId);
                    
                    // Attempt to start a DirectLine conversation with the bot
                    conversation = await callAutomationService.StartConversationAsync();
                    
                    logger.LogInformation("✅ DirectLine conversation created successfully: ConversationId={ConversationId}, StreamUrl={HasStreamUrl}", 
                        conversation?.ConversationId, !string.IsNullOrEmpty(conversation?.StreamUrl));
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("403"))
                {
                    // If the primary method fails with authentication issues, try the token-based approach
                    logger.LogWarning("⚠️ Regular StartConversationAsync failed with 403, trying token method for call {CorrelationId}: {Error}", 
                        correlationId, ex.Message);
                    
                    try
                    {
                        conversation = await callAutomationService.StartConversationWithTokenAsync();
                        logger.LogInformation("✅ Token-based DirectLine conversation created: ConversationId={ConversationId}", 
                            conversation?.ConversationId);
                    }
                    catch (Exception tokenEx)
                    {
                        logger.LogError(tokenEx, "❌ Both DirectLine authentication methods failed for call {CorrelationId}", correlationId);
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "❌ DirectLine conversation creation failed for call {CorrelationId}: {Error}", correlationId, ex.Message);
                    throw;
                }
                
                if (conversation == null || string.IsNullOrEmpty(conversation.ConversationId))
                {
                    logger.LogError("❌ Failed to get valid conversation for call {CorrelationId}: conversation={IsNull}, conversationId={ConversationId}", 
                        correlationId, conversation == null, conversation?.ConversationId ?? "NULL");
                    throw new InvalidOperationException("Failed to get valid conversation");
                }
                
                // Associate the bot conversation with this call
                var conversationId = conversation.ConversationId;
                if (callStore.ContainsKey(correlationId))
                {
                    callStore[correlationId].ConversationId = conversationId;
                    logger.LogInformation("🔗 Associated conversation {ConversationId} with call {CorrelationId}", conversationId, correlationId);
                }
                else
                {
                    logger.LogWarning("⚠️ Call {CorrelationId} not found in call store when associating conversation", correlationId);
                }

                // Start listening for bot responses asynchronously
                var cts = new CancellationTokenSource();
                callAutomationService.RegisterTokenSource(correlationId, cts);
                logger.LogInformation("📡 Registered token source for call {CorrelationId}, active token sources: {ActiveCount}", 
                    correlationId, callAutomationService.GetActiveTokenSourceCount());
                
                // Parallel optimization: Start WebSocket listener and send greeting simultaneously
                Task webSocketTask = Task.CompletedTask;
                
                // Validate that we have a WebSocket URL for real-time bot communication
                if (string.IsNullOrEmpty(conversation.StreamUrl))
                {
                    logger.LogError("❌ StreamUrl is null or empty for call {CorrelationId}, cannot listen to bot", correlationId);
                }
                else
                {
                    logger.LogInformation("🔄 Starting bot WebSocket listener for call {CorrelationId}, StreamUrl: {StreamUrl}", 
                        correlationId, conversation.StreamUrl);
                    
                    // Start the bot listener immediately (parallel with greeting)
                    webSocketTask = Task.Run(async () => 
                    {
                        try
                        {
                            await callAutomationService.ListenToBotWebSocketAsync(conversation.StreamUrl, callConnection, cts.Token);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "❌ WebSocket listener failed for call {CorrelationId}: {Error}", correlationId, ex.Message);
                        }
                    });
                }

                // Send initial greeting message to the bot in parallel with WebSocket setup
                logger.LogInformation("💬 Sending initial greeting to bot for call {CorrelationId}", correlationId);
                Task greetingTask = Task.Run(async () =>
                {
                    try
                    {
                        await callAutomationService.SendMessageAsync(conversationId, "Hello");
                        logger.LogInformation("✅ Initial greeting sent successfully for call {CorrelationId}", correlationId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "❌ Failed to send initial greeting for call {CorrelationId}: {Error}", correlationId, ex.Message);
                    }
                });

                // Wait briefly to ensure both operations are initiated (don't block the webhook response)
                _ = Task.WhenAll(webSocketTask, greetingTask);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ CRITICAL: Error processing CallConnected event for call {CorrelationId}: {Message}", correlationId, ex.Message);
                
                // Log additional debugging information
                logger.LogError("🔍 CallConnected debugging info - Call store count: {CallStoreCount}, Active token sources: {TokenSources}", 
                    callStore.Count, callAutomationService.GetActiveTokenSourceCount());
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
            logger.LogInformation("Call Disconnected - Remaining active calls: {ActiveCallCount}, Disconnected call correlation ID: {CorrelationId}", 
                callStore.Count > 0 ? callStore.Count - 1 : 0, correlationId);
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
if (app.Environment.IsDevelopment())
{
    // Enable Swagger UI for API documentation and testing in Development only
    app.UseSwagger();
    
    // Add security middleware for Swagger endpoints
    app.UseWhen(context => context.Request.Path.StartsWithSegments("/swagger"), appBuilder =>
    {
        appBuilder.Use(async (context, next) =>
        {
            // Check for API key authentication for all Swagger access
            if (string.IsNullOrEmpty(healthCheckApiKey) ||
                !context.Request.Headers.ContainsKey("X-API-Key") ||
                context.Request.Headers["X-API-Key"] != healthCheckApiKey)
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync(@"
<!DOCTYPE html>
<html>
<head>
    <title>Unauthorized - Swagger Access</title>
    <style>
        body { font-family: Arial, sans-serif; margin: 40px; background-color: #f5f5f5; }
        .container { background-color: white; padding: 40px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); max-width: 600px; margin: 0 auto; }
        .error { color: #d13438; font-size: 18px; margin-bottom: 20px; }
        .code { background-color: #f8f9fa; padding: 10px; border-radius: 4px; font-family: monospace; }
        .note { color: #666; font-size: 14px; margin-top: 20px; }
    </style>
</head>
<body>
    <div class='container'>
        <h1>🔒 Swagger API Documentation - Authentication Required</h1>
        <div class='error'>❌ Unauthorized Access</div>
        <p>Access to API documentation requires authentication with the <strong>X-API-Key</strong> header.</p>
        <p><strong>Example:</strong></p>
        <div class='code'>curl -H ""X-API-Key: your-api-key"" https://acsformcs.azurewebsites.net/swagger</div>
        <div class='note'>
            <strong>Note:</strong> This is the Development environment. The API key is stored in Azure Key Vault as 'HealthCheckApiKey'.
            <br>Production deployments have Swagger documentation completely disabled for security.
        </div>
    </div>
</body>
</html>");
                return;
            }
            
            await next();
        });
    });
    
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "ACS for MCS API v1.0");
        c.RoutePrefix = "swagger"; // Serve the Swagger UI at /swagger
        c.DocumentTitle = "ACS for MCS API Documentation (Development)";
        c.DisplayRequestDuration();
        c.EnableTryItOutByDefault();
        c.EnableFilter();
        c.ShowExtensions();
        c.EnableDeepLinking();
        
        // Custom CSS for better styling
        c.InjectStylesheet("/swagger-ui/custom.css");
        
        // Add custom JavaScript for enhanced functionality
        c.InjectJavascript("/swagger-ui/custom.js");
        
        // Configure OAuth if needed (currently using API Key)
        c.OAuthClientId("swagger-ui");
        c.OAuthAppName("ACS for MCS Swagger UI");
        
        // Show API key input prominently
        c.DefaultModelsExpandDepth(1);
        c.DefaultModelExpandDepth(1);
        c.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
        
        // Custom header for API documentation with security notice
        c.HeadContent = @"
        <style>
            .swagger-ui .topbar { background-color: #0078d4; }
            .swagger-ui .topbar .topbar-wrapper .link { color: white; }
            .swagger-ui .info .title { color: #0078d4; }
            .swagger-ui .info .description { color: #d13438; font-weight: bold; }
            .security-notice { color: #d13438; font-weight: bold; background: #fff3cd; padding: 10px; border: 1px solid #ffeaa7; border-radius: 4px; margin: 10px 0; }
        </style>";
    });
}
// Production: Swagger UI is completely disabled for security

// Enable authorization middleware (though not heavily used in this application)
app.UseAuthorization();

// Map controller routes for any additional API controllers
app.MapControllers();

// Health check endpoints are configured below based on environment
// This avoids duplicate registrations that cause AmbiguousMatchException

// Add concurrent call monitoring endpoint - now secured in both environments
if (app.Environment.IsDevelopment())
{
    // Development: Secured health endpoints with API key authentication (consistent with Production)
    app.MapGet("/health/calls", async (HttpContext context, IServiceProvider serviceProvider) =>
    {
        // Check for API key authentication (same as Production)
        if (string.IsNullOrEmpty(healthCheckApiKey) ||
            !context.Request.Headers.ContainsKey("X-API-Key") ||
            context.Request.Headers["X-API-Key"] != healthCheckApiKey)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var callAutomationService = serviceProvider.GetRequiredService<CallAutomationService>();
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        
        try
        {
            var activeCalls = callAutomationService.GetActiveCallCount();
            var callDetails = callAutomationService.GetCallStatistics();
            
            logger.LogInformation("Authenticated health check requested - Active calls: {ActiveCalls}", activeCalls);
            
            await context.Response.WriteAsJsonAsync(new
            {
                timestamp = DateTime.UtcNow,
                activeCalls = activeCalls,
                statistics = callDetails, // Full details available in Development for debugging
                status = activeCalls < 50 ? "healthy" : "warning",
                maxRecommendedCalls = 45,
                environment = "Development"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving call statistics for health check");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Error retrieving call statistics");
        }
    })
    .WithName("GetDevelopmentCallMonitoring")
    .WithTags("Health Checks", "Monitoring")
    .WithSummary("Concurrent Call Monitoring (Development)")
    .WithDescription("Returns detailed information about active calls and concurrent call statistics. Requires X-API-Key header for authentication.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status500InternalServerError);

    // Development: Secured system metrics endpoint
    app.MapGet("/health/metrics", async (HttpContext context, IServiceProvider serviceProvider) =>
    {
        // Check for API key authentication
        if (string.IsNullOrEmpty(healthCheckApiKey) ||
            !context.Request.Headers.ContainsKey("X-API-Key") ||
            context.Request.Headers["X-API-Key"] != healthCheckApiKey)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var callAutomationService = serviceProvider.GetRequiredService<CallAutomationService>();
        
        try
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var gc = GC.GetTotalMemory(false);
            var activeCalls = callAutomationService.GetActiveCallCount();
            
            logger.LogInformation("System metrics requested via authenticated endpoint");
            
            await context.Response.WriteAsJsonAsync(new
            {
                timestamp = DateTime.UtcNow,
                environment = "Development",
                systemMetrics = new
                {
                    processId = process.Id,
                    workingSet = process.WorkingSet64,
                    gcMemory = gc,
                    threadCount = process.Threads.Count,
                    startTime = process.StartTime,
                    uptime = DateTime.UtcNow - process.StartTime
                },
                applicationMetrics = new
                {
                    activeCalls = activeCalls,
                    maxConcurrentCalls = 45,
                    callCapacityUsed = Math.Round((double)activeCalls / 45 * 100, 2)
                },
                status = new
                {
                    overall = activeCalls < 40 ? "healthy" : activeCalls < 50 ? "warning" : "critical",
                    memoryPressure = gc > 100_000_000 ? "high" : "normal",
                    threadUtilization = process.Threads.Count > 50 ? "high" : "normal"
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving system metrics");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Error retrieving system metrics");
        }
    })
    .WithName("GetDevelopmentSystemMetrics")
    .WithTags("Health Checks", "Monitoring")
    .WithSummary("System Performance Metrics (Development)")
    .WithDescription("Returns detailed system performance metrics including memory usage, CPU metrics, and application statistics. Requires X-API-Key header for authentication.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status500InternalServerError);

    // Development: Secured configuration status endpoint
    app.MapGet("/health/config", async (HttpContext context, IServiceProvider serviceProvider) =>
    {
        // Check for API key authentication
        if (string.IsNullOrEmpty(healthCheckApiKey) ||
            !context.Request.Headers.ContainsKey("X-API-Key") ||
            context.Request.Headers["X-API-Key"] != healthCheckApiKey)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var appSettings = serviceProvider.GetRequiredService<IOptions<AppSettings>>().Value;
        
        try
        {
            logger.LogInformation("Configuration status requested via authenticated endpoint");
            
            await context.Response.WriteAsJsonAsync(new
            {
                timestamp = DateTime.UtcNow,
                environment = "Development",
                configurationStatus = new
                {
                    keyVaultEndpoint = !string.IsNullOrEmpty(configuration["KeyVault:Endpoint"]),
                    acsConnectionString = !string.IsNullOrEmpty(configuration["AcsConnectionString"]),
                    directLineSecret = !string.IsNullOrEmpty(configuration["DirectLineSecret"]),
                    baseUri = !string.IsNullOrEmpty(appSettings.BaseUri),
                    cognitiveServiceEndpoint = !string.IsNullOrEmpty(configuration["CognitiveServiceEndpoint"]),
                    agentPhoneNumber = !string.IsNullOrEmpty(configuration["AgentPhoneNumber"])
                },
                securityStatus = new
                {
                    httpsOnly = true,
                    managedIdentity = true,
                    apiKeyProtected = !string.IsNullOrEmpty(healthCheckApiKey),
                    keyVaultRbac = true
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving configuration status");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Error retrieving configuration status");
        }
    })
    .WithName("GetDevelopmentConfigurationStatus")
    .WithTags("Health Checks", "Configuration")
    .WithSummary("Configuration Validation (Development)")
    .WithDescription("Returns the status of all required configuration values and security settings. Requires X-API-Key header for authentication.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status500InternalServerError);
}
else
{
    // Production: Minimal configuration for maximum performance
    // All health and monitoring endpoints are disabled to reduce overhead and attack surface
    // Only essential business endpoints are available
    
    // Note: For production monitoring, use Application Insights, Azure Monitor, or external monitoring tools
    // This eliminates the overhead of in-app health checks while providing better monitoring capabilities
}

// Add secured health check endpoints for both environments
if (app.Environment.IsDevelopment())
{
    // Development: Secured basic health endpoint
    app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions()
    {
        ResponseWriter = async (context, report) =>
        {
            // Check for API key authentication
            if (string.IsNullOrEmpty(healthCheckApiKey) ||
                !context.Request.Headers.ContainsKey("X-API-Key") ||
                context.Request.Headers["X-API-Key"] != healthCheckApiKey)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }

            context.Response.ContentType = "application/json";
            var result = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = report.Status.ToString(),
                timestamp = DateTime.UtcNow,
                environment = "Development",
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    exception = entry.Value.Exception?.Message,
                    data = entry.Value.Data,
                    description = entry.Value.Description,
                    duration = entry.Value.Duration
                })
            });
            await context.Response.WriteAsync(result);
        }
    });

    // Development: Secured detailed health endpoint
    app.MapHealthChecks("/health/detailed", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions()
    {
        ResponseWriter = async (context, report) =>
        {
            // Check for API key authentication
            if (string.IsNullOrEmpty(healthCheckApiKey) ||
                !context.Request.Headers.ContainsKey("X-API-Key") ||
                context.Request.Headers["X-API-Key"] != healthCheckApiKey)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized - API Key Required");
                return;
            }

            context.Response.ContentType = "application/json";
            var result = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = report.Status.ToString(),
                timestamp = DateTime.UtcNow,
                environment = "Development",
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    exception = entry.Value.Exception?.Message,
                    data = entry.Value.Data,
                    description = entry.Value.Description,
                    duration = entry.Value.Duration
                })
            });
            await context.Response.WriteAsync(result);
        }
    });
}

// Production: No health check endpoints - monitoring disabled for maximum performance
// Use Application Insights, Azure Monitor, or external tools for production monitoring

#endregion

// Log startup configuration information after app is built
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();

// Log Key Vault configuration status
var kvEndpoint = builder.Configuration["KeyVault:Endpoint"];
if (!string.IsNullOrEmpty(kvEndpoint))
{
    startupLogger.LogInformation("Azure Key Vault configuration provider added for endpoint: {KeyVaultEndpoint}", kvEndpoint);
}
else
{
    startupLogger.LogWarning("Key Vault endpoint not configured, using local configuration values");
}

// Log environment configuration
var envName = app.Environment.EnvironmentName;
startupLogger.LogInformation("Application starting in {Environment} environment", envName);

// Check if we can load BaseUri configuration
var configuredBaseUri = builder.Configuration["BaseUri"];
if (!string.IsNullOrEmpty(configuredBaseUri))
{
    startupLogger.LogInformation("BaseUri configured: {BaseUri}", configuredBaseUri);
}

startupLogger.LogInformation("Application startup configuration complete");

// Start the application and begin listening for requests
app.Run();

// Helper method for async Key Vault operations
static async Task<string?> GetBaseUriFromKeyVaultAsync(IConfiguration configuration, string secretName)
{
    try
    {
        // Attempt to retrieve the environment-specific BaseUri from Azure Key Vault
        var keyVaultUriString = configuration["KeyVault:Endpoint"];
        if (string.IsNullOrEmpty(keyVaultUriString))
        {
            // Will be logged when logger becomes available
            return null;
        }
        
        var keyVaultUri = new Uri(keyVaultUriString);
        var secretClient = new SecretClient(keyVaultUri, new DefaultAzureCredential());
        var secret = await secretClient.GetSecretAsync(secretName);
        var baseUri = secret.Value.Value?.TrimEnd('/');
        // Success will be logged when logger becomes available
        return baseUri ?? throw new InvalidOperationException($"BaseUri secret '{secretName}' returned null or empty value");
    }
    catch (Exception)
    {
        // Error will be logged when logger becomes available  
        // Return null to indicate failure - caller will handle fallback
        return null;
    }
}

static async Task<string?> GetSecretFromKeyVaultAsync(IConfiguration configuration, string secretName)
{
    try
    {
        var keyVaultUriString = configuration["KeyVault:Endpoint"];
        if (string.IsNullOrEmpty(keyVaultUriString))
        {
            // Use a logging approach that will appear in Azure Web App logs
            System.Diagnostics.Debug.WriteLine($"Warning: KeyVault:Endpoint not configured");
            return null;
        }
        
        var keyVaultUri = new Uri(keyVaultUriString);
        var secretClient = new SecretClient(keyVaultUri, new DefaultAzureCredential());
        var secret = await secretClient.GetSecretAsync(secretName);
        var secretValue = secret.Value.Value;
        System.Diagnostics.Debug.WriteLine($"Loaded secret '{secretName}' from Key Vault");
        return secretValue;
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"Warning: Failed to load {secretName} from Key Vault: {ex.Message}");
        return null;
    }
}
