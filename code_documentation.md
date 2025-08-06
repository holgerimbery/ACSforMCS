# ACSforMCS - Complete Code Documentation

## Table of Contents
1. [Project Overview](#project-overview)
2. [Architecture](#architecture)
3. [Core Components](#core-components)
4. [Configuration](#configuration)
5. [API Endpoints](#api-endpoints)
6. [Data Models](#data-models)
7. [Services](#services)
8. [Middleware](#middleware)
9. [Health Checks](#health-checks)
10. [Deployment](#deployment)
11. [Development Setup](#development-setup)
12. [Usage Examples](#usage-examples)
13. [Troubleshooting](#troubleshooting)

## Project Overview

**ACSforMCS** (Azure Communication Services for Microsoft Customer Service) is a .NET 9 web application that integrates Azure Communication Services with Microsoft Bot Framework to provide voice-enabled customer service capabilities. The application enables callers to interact with AI-powered bots through natural speech, with real-time transcription and text-to-speech conversion.

### Key Features
- **Voice-enabled bot conversations** through phone calls
- **Real-time speech-to-text** transcription using Azure Cognitive Services
- **Text-to-speech** conversion for bot responses
- **Call transfer capabilities** to human agents
- **WebSocket-based real-time communication** with Bot Framework
- **Azure Key Vault integration** for secure configuration management
- **Health monitoring** and diagnostics

### Technology Stack
- **.NET 9** - Core framework
- **Azure Communication Services** - Call automation and telephony
- **Azure Cognitive Services** - Speech recognition and synthesis
- **Microsoft Bot Framework** - Conversational AI integration
- **Azure Key Vault** - Secure configuration management
- **WebSockets** - Real-time communication
- **ASP.NET Core** - Web framework
- **Swagger/OpenAPI** - API documentation

## Architecture

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   Phone Call    │───▶│   ACS Gateway   │───▶│  ACSforMCS App  │
└─────────────────┘    └─────────────────┘    └─────────────────┘
                                                       │
                                                       ▼
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│ Azure Key Vault │◀───│ Configuration   │◀───│  Program.cs     │
└─────────────────┘    └─────────────────┘    └─────────────────┘
                                                       │
                                                       ▼
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   Bot Framework │◀───│ DirectLine API  │◀───│CallAutomation   │
│      Bot        │    │                 │    │    Service      │
└─────────────────┘    └─────────────────┘    └─────────────────┘
         │                       │                       │
         ▼                       ▼                       ▼
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   WebSocket     │───▶│  WebSocket      │───▶│   Call Media    │
│   Listener      │    │  Middleware     │    │   Operations    │
└─────────────────┘    └─────────────────┘    └─────────────────┘
```

### Data Flow
1. **Incoming Call** → ACS receives call and triggers EventGrid webhook
2. **Call Connection** → Application answers call and starts transcription
3. **Bot Conversation** → DirectLine conversation initiated with Bot Framework
4. **Speech Processing** → Audio transcribed to text and sent to bot
5. **Bot Response** → Bot responses converted to speech and played to caller
6. **Call Management** → Transfer or termination based on bot decisions

## Core Components

### Program.cs
The main entry point that configures and bootstraps the application.

**Key Responsibilities:**
- Azure Key Vault configuration
- Dependency injection setup
- HTTP client configuration with retry policies
- API endpoint definitions
- WebSocket middleware registration
- Health check configuration

**Critical Configuration:**
```csharp
// Azure Key Vault integration
builder.Configuration.AddAzureKeyVault(
    new Uri(keyVaultEndpoint),
    new DefaultAzureCredential());

// HTTP client with retry policies
builder.Services.AddHttpClient("DirectLine", client => {
    client.BaseAddress = new Uri(Constants.DirectLineBaseUrl);
    client.DefaultRequestHeaders.Authorization = 
        new AuthenticationHeaderValue("Bearer", appSettings.DirectLineSecret.Trim());
})
.AddTransientHttpErrorPolicy(policy => 
    policy.WaitAndRetryAsync(3, retryAttempt => 
        TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));
```

### CallAutomationService.cs
The core orchestration service that manages the entire call lifecycle.

**Key Methods:**
- `StartConversationAsync()` - Initiates DirectLine conversation
- `SendMessageAsync()` - Forwards speech-to-text to bot
- `ListenToBotWebSocketAsync()` - Processes real-time bot responses
- `PlayToAllAsync()` - Converts text to speech and plays to caller
- `TransferCallToPhoneNumberAsync()` - Handles call transfers
- `ExtractLatestAgentActivity()` - Parses bot response messages

**Bot Communication Flow:**
```csharp
// Start bot conversation
var conversation = await StartConversationAsync();

// Listen for bot responses
_ = Task.Run(() => ListenToBotWebSocketAsync(
    conversation.StreamUrl, callConnection, cancellationToken));

// Send user message to bot
await SendMessageAsync(conversationId, transcribedText);
```

### WebSocketMiddleware.cs
Handles real-time audio streaming from Azure Communication Services.

**Key Features:**
- WebSocket connection management for ACS audio streams
- Real-time speech-to-text processing
- Message fragmentation handling
- Call correlation and context management

**Processing Pipeline:**
1. Validates WebSocket upgrade requests
2. Extracts call correlation and connection IDs
3. Receives streaming audio data
4. Processes transcription results
5. Forwards final transcriptions to bot conversation

## Configuration

### AppSettings.cs
Strongly-typed configuration class containing all application settings.

```csharp
public class AppSettings
{
    public string AcsConnectionString { get; set; } = string.Empty;
    public string CognitiveServiceEndpoint { get; set; } = string.Empty;
    public string AgentPhoneNumber { get; set; } = string.Empty;
    public string DirectLineSecret { get; set; } = string.Empty;
    public string BaseUri { get; set; } = string.Empty;
    public string DefaultTransferNumber { get; set; } = string.Empty;
}

public class VoiceOptions
{
    public string VoiceName { get; set; } = "en-US-NancyNeural";
    public string Language { get; set; } = "en-US";
}
```

### Environment-Specific Configuration

**Development (appsettings.Development.json):**
- Enhanced logging for debugging
- Local secret management options
- Development tunnel support

**Production (appsettings.Production.json):**
- Optimized logging levels
- Azure DevOps token replacement
- Production Key Vault integration

### Azure Key Vault Secrets
Required secrets stored in Azure Key Vault:
- `AcsConnectionString` - Azure Communication Services connection
- `CognitiveServiceEndpoint` - Speech services endpoint
- `DirectLineSecret` - Bot Framework DirectLine API key
- `AgentPhoneNumber` - Default transfer phone number
- `BaseUri-Development` - Development environment URL
- `BaseUri-Production` - Production environment URL

## API Endpoints

### POST /api/incomingCall
**Purpose:** Webhook endpoint for incoming call events from Azure Communication Services.

**Event Types:**
- EventGrid subscription validation
- Incoming call notifications

**Processing Flow:**
1. Validates EventGrid events
2. Extracts incoming call context
3. Configures call answering options
4. Starts real-time transcription
5. Creates call context for tracking

**Example Request:**
```json
[
  {
    "eventType": "Microsoft.Communication.IncomingCall",
    "data": {
      "incomingCallContext": "eyJ...",
      "to": "+1234567890",
      "from": "+0987654321"
    }
  }
]
```

### POST /api/calls/{contextId}
**Purpose:** Webhook endpoint for call automation events throughout the call lifecycle.

**Event Types:**
- `CallConnected` - Call successfully established
- `CallDisconnected` - Call ended
- `CallTransferAccepted` - Transfer completed
- `CallTransferFailed` - Transfer failed
- `PlayCompleted` - Audio playback finished
- `TranscriptionStarted` - Speech recognition active

**CallConnected Processing:**
1. Starts DirectLine conversation with bot
2. Initiates WebSocket listener for bot responses
3. Sends initial greeting to bot
4. Sets up call context and cancellation tokens

### GET /health
**Purpose:** Health check endpoint for monitoring system status.

**Checks:**
- DirectLine API connectivity
- Azure service availability
- Application readiness

## Data Models

### CallContext.cs
Links ACS calls with Bot Framework conversations.
```csharp
public class CallContext
{
    public string? CorrelationId { get; set; }    // ACS call identifier
    public string? ConversationId { get; set; }   // Bot conversation ID
}
```

### AgentActivity.cs
Represents parsed bot responses for processing.
```csharp
public class AgentActivity
{
    public string? Type { get; set; }  // Activity type (message, transfer, error)
    public string? Text { get; set; }  // Activity content
}
```

### Conversation.cs
DirectLine API conversation response structure.
```csharp
public class Conversation
{
    public string? ConversationId { get; set; }      // Unique conversation ID
    public string? Token { get; set; }               // Optional auth token
    public string? StreamUrl { get; set; }           // WebSocket URL
    public string? ReferenceGrammarId { get; set; }  // Speech recognition grammar
}
```

## Services

### CallAutomationService
**Primary Responsibilities:**
- DirectLine conversation management
- WebSocket communication with bots
- Audio playback coordination
- Call transfer orchestration
- Message parsing and processing

**Key Integration Points:**
- Azure Communication Services CallAutomation API
- Bot Framework DirectLine API
- Azure Cognitive Services Speech API
- WebSocket real-time communication

**Error Handling:**
- Retry policies for HTTP requests
- Graceful WebSocket disconnection
- Bot response error categorization
- User-friendly error message conversion

## Middleware

### WebSocketMiddleware
**Purpose:** Handles WebSocket connections for real-time audio streaming from ACS.

**Features:**
- Call correlation ID validation
- Message fragmentation handling
- Transcription result processing
- Context-aware message routing

**Processing Logic:**
1. Validates WebSocket upgrade requests
2. Extracts call identification headers
3. Manages message assembly across frames
4. Processes intermediate vs. final transcriptions
5. Routes messages to appropriate conversations

## Health Checks

### DirectLineHealthCheck
**Monitoring:** Bot Framework DirectLine API availability.

**Implementation:**
- Pings DirectLine service endpoint
- Validates authentication
- Returns health status with descriptive messages

**Health States:**
- **Healthy:** Service responding correctly
- **Degraded:** Service responding with errors
- **Unhealthy:** Service unreachable or authentication failed

## Deployment

### Azure Infrastructure Requirements

**Core Services:**
- Azure Communication Services resource
- Azure Cognitive Services (Speech)
- Azure Key Vault
- Azure App Service
- Azure Bot Service registration

**Optional Services:**
- Application Insights (monitoring)
- Azure Storage (logging)
- Azure Front Door (CDN/WAF)

### Deployment Pipeline

**Azure DevOps Configuration:**
1. Variable groups with environment-specific values
2. Token replacement for configuration files
3. Key Vault secret deployment
4. Application deployment to App Service


### Testing

**Local Testing:**
- Use ngrok Visual Studio dev tunnels for webhook testing
- Configure ACS phone number to point to tunnel URL
- Test with actual phone calls or ACS SDK

**Unit Testing:**
- Mock Azure service clients
- Test configuration validation
- Verify message parsing logic

## Usage Examples

### Basic Call Flow

1. **Caller dials ACS phone number**
2. **ACS triggers webhook to `/api/incomingCall`**
3. **Application answers call and starts transcription**
4. **Bot conversation begins with greeting**
5. **Caller speaks, speech is transcribed and sent to bot**
6. **Bot responds, text is converted to speech and played**
7. **Conversation continues until completion or transfer**

### Bot Response Types

**Regular Message:**
```json
{
  "type": "message",
  "text": "Hello! How can I help you today?",
  "speak": "Hello! How can I help you today?"
}
```

**Transfer Command:**
```json
{
  "type": "message",
  "text": "TRANSFER:+1234567890:Transferring you to a specialist"
}
```

**End Conversation:**
```json
{
  "type": "endOfConversation",
  "text": "Thank you for calling. Goodbye!"
}
```

## Troubleshooting

### Common Issues

**1. WebSocket Connection Failures**
- Verify Base URI configuration
- Check firewall and network settings
- Ensure WebSocket support in hosting environment

**2. DirectLine Authentication Errors**
- Validate DirectLine secret
- Check Bot Framework registration
- Verify channel configuration

**3. Speech Recognition Issues**
- Confirm Cognitive Services endpoint
- Check language and region settings
- Verify audio quality and encoding

**4. Call Transfer Failures**
- Validate phone number format (E.164)
- Check ACS phone number capabilities
- Verify transfer permissions

### Debugging Tips

**Logging Configuration:**
```json
{
  "Logging": {
    "LogLevel": {
      "ACSforMCS": "Debug",
      "Azure.Communication": "Information",
      "System.Net.Http.HttpClient": "Information"
    }
  }
}
```

**Health Check Monitoring:**
```bash
# Check application health
curl https://your-app.azurewebsites.net/health

# Check DirectLine connectivity
curl https://your-app.azurewebsites.net/health/ready
```

**Event Tracing:**
- Use correlation IDs to trace call flows
- Monitor Azure service logs
- Enable Application Insights for detailed telemetry

### Performance Optimization

**Best Practices:**
- Use connection pooling for HTTP clients
- Implement proper cancellation token usage
- Monitor Azure service quotas and limits
- Optimize WebSocket message processing

**Scaling Considerations:**
- Use Azure App Service auto-scaling
- Monitor concurrent call limits
- Consider regional deployment for latency
- Implement proper resource cleanup

---

**Note:** This documentation is current as of August 2025. For the latest updates and changes, refer to the project's changelog.md and commit history.