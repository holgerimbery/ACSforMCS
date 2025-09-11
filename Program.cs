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
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "ACS for MCS API",
        Version = "v1",
        Description = "Azure Communication Services integration with Microsoft Copilot Studio for telephony automation",
        Contact = new Microsoft.OpenApi.Models.OpenApiContact
        {
            Name = "ACS for MCS",
            Url = new Uri("https://github.com/holgerimbery/ACSforMCS")
        }
    });

    // Configure API Key authentication for Swagger
    c.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "X-API-Key",
        Description = "API Key for accessing production monitoring endpoints"
    });

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
}); // Add Swagger/OpenAPI documentation generation with enhanced configuration

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

// Handle base URI configuration with support for development tunneling and environment-specific values
// This supports both local development (with VS tunnels) and production deployment scenarios
var baseUri = Environment.GetEnvironmentVariable("VS_TUNNEL_URL")?.TrimEnd('/');
if (string.IsNullOrEmpty(baseUri))
{
    // Get environment-specific BaseUri from Key Vault
    // This allows different URLs for Development vs Production environments
    var environment = builder.Environment.EnvironmentName; // Gets "Development" or "Production"
    var secretName = $"BaseUri-{environment}";
    
    baseUri = await GetBaseUriFromKeyVaultAsync(builder.Configuration, secretName);
    
    // Fall back to the general BaseUri configuration if environment-specific one is not available
    if (string.IsNullOrEmpty(baseUri))
    {
        baseUri = appSettings.BaseUri?.TrimEnd('/');
    }
    
    ArgumentNullException.ThrowIfNullOrEmpty(baseUri, "BaseUri not found in Key Vault or configuration");
}
appSettings.BaseUri = baseUri;

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
    
    // Set a reasonable timeout for API calls
    client.Timeout = TimeSpan.FromSeconds(30);
    
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

// Reduce WebSocket message processing noise (set to Information for normal operation)
builder.Logging.AddFilter("ACSforMCS.Services.CallAutomationService", LogLevel.Information);
builder.Logging.AddFilter("ACSforMCS.Middleware.WebSocketMiddleware", LogLevel.Information);

#endregion

var app = builder.Build();

// Load Health Check API Key from Key Vault for production security
if (builder.Environment.IsProduction())
{
    healthCheckApiKey = await GetSecretFromKeyVaultAsync(builder.Configuration, "HealthCheckApiKey");
    if (string.IsNullOrEmpty(healthCheckApiKey))
    {
        Console.WriteLine("Warning: HealthCheckApiKey not found in Key Vault - health endpoints will be disabled");
    }
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
                
                // Log concurrent call metrics
                logger.LogInformation("Call context created - Total active calls: {ActiveCalls}, Correlation ID: {CorrelationId}",
                    callStore.Count, correlationId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Answer call exception: {Message}", ex.Message);
        }
    }
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
                logger.LogInformation("Call connected - Active calls: {ActiveCallCount}, New call correlation ID: {CorrelationId}", 
                    callStore.Count, correlationId);
                
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

// Add concurrent call monitoring endpoint
if (app.Environment.IsDevelopment())
{
    // Development: Unrestricted access for debugging
    app.MapGet("/health/calls", (IServiceProvider serviceProvider) =>
    {
        var callAutomationService = serviceProvider.GetRequiredService<CallAutomationService>();
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        
        try
        {
            var activeCalls = callAutomationService.GetActiveCallCount();
            var callDetails = callAutomationService.GetCallStatistics();
            
            logger.LogInformation("Health check requested - Active calls: {ActiveCalls}", activeCalls);
            
            return Results.Ok(new
            {
                timestamp = DateTime.UtcNow,
                activeCalls = activeCalls,
                statistics = callDetails,
                status = activeCalls < 50 ? "healthy" : "warning",
                maxRecommendedCalls = 45,
                environment = "Development"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving call statistics for health check");
            return Results.Problem("Error retrieving call statistics");
        }
    })
    .WithName("GetCallMonitoring")
    .WithTags("Health Checks", "Monitoring")
    .WithSummary("Concurrent Call Monitoring")
    .WithDescription("Returns detailed information about active calls and concurrent call statistics for development debugging")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status500InternalServerError);
}
else
{
    // Production: Secured endpoint with API key authentication
    app.MapGet("/health/calls", async (HttpContext context, IServiceProvider serviceProvider) =>
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
                statistics = new
                {
                    totalActiveCalls = ((dynamic)callDetails).totalActiveCalls,
                    activeTokenSources = ((dynamic)callDetails).activeTokenSources
                    // Exclude detailed call information for security
                },
                status = activeCalls < 50 ? "healthy" : "warning",
                maxRecommendedCalls = 45,
                environment = "Production"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving call statistics for health check");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Error retrieving call statistics");
        }
    })
    .WithName("GetProductionCallMonitoring")
    .WithTags("Health Checks", "Monitoring")
    .WithSummary("Concurrent Call Monitoring (Production)")
    .WithDescription("Returns sanitized information about active calls and concurrent call statistics. Requires X-API-Key header for authentication.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status500InternalServerError);

    // Production: Secured system metrics endpoint
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
                environment = "Production",
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
    .WithName("GetSystemMetrics")
    .WithTags("Health Checks", "Monitoring")
    .WithSummary("System Performance Metrics")
    .WithDescription("Returns detailed system performance metrics including memory usage, CPU metrics, and application statistics. Requires X-API-Key header for authentication.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status500InternalServerError);

    // Production: Secured configuration status endpoint
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
        
        try
        {
            logger.LogInformation("Configuration status requested via authenticated endpoint");
            
            await context.Response.WriteAsJsonAsync(new
            {
                timestamp = DateTime.UtcNow,
                environment = "Production",
                configurationStatus = new
                {
                    keyVaultEndpoint = !string.IsNullOrEmpty(configuration["KeyVault:Endpoint"]),
                    acsConnectionString = !string.IsNullOrEmpty(configuration["AcsConnectionString"]),
                    directLineSecret = !string.IsNullOrEmpty(configuration["DirectLineSecret"]),
                    baseUri = !string.IsNullOrEmpty(configuration["BaseUri"]),
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
    .WithName("GetConfigurationStatus")
    .WithTags("Health Checks", "Configuration")
    .WithSummary("Configuration Validation")
    .WithDescription("Returns the status of all required configuration values and security settings. Requires X-API-Key header for authentication.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status500InternalServerError);
}

// Add detailed health check endpoint for debugging (DEVELOPMENT ONLY for security)
if (app.Environment.IsDevelopment())
{
    app.MapHealthChecks("/health/detailed", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions()
    {
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            var result = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = report.Status.ToString(),
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
else
{
    // Production: Secured detailed health endpoint with API key authentication
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
                environment = "Production",
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

    // Production: Provide a secure basic health endpoint that requires authentication
    app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions()
    {
        ResponseWriter = async (context, report) =>
        {
            // Check for basic authentication or API key
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
                // Sanitized output - no sensitive details in production
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    // No exception details or internal data in production
                    duration = entry.Value.Duration
                })
            });
            await context.Response.WriteAsync(result);
        }
    }).RequireAuthorization(); // Add authorization requirement
}

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
            Console.WriteLine($"Warning: KeyVault:Endpoint not configured");
            return null;
        }
        
        var keyVaultUri = new Uri(keyVaultUriString);
        var secretClient = new SecretClient(keyVaultUri, new DefaultAzureCredential());
        var secret = await secretClient.GetSecretAsync(secretName);
        var secretValue = secret.Value.Value;
        Console.WriteLine($"Loaded secret '{secretName}' from Key Vault");
        return secretValue;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Failed to load {secretName} from Key Vault: {ex.Message}");
        return null;
    }
}
