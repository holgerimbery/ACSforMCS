namespace ACSforMCS
{
    /// <summary>
    /// Represents the contextual information that links an Azure Communication Services call
    /// with its corresponding Bot Framework conversation. This class serves as a bridge between
    /// the telephony infrastructure and the conversational AI components.
    /// 
    /// CallContext instances are stored in a thread-safe dictionary during active calls to
    /// enable proper routing of transcribed speech to the correct bot conversation and
    /// ensure bot responses are played back to the right caller.
    /// </summary>
    public class CallContext
    {
        /// <summary>
        /// Unique identifier that correlates an ACS call with its associated resources and events.
        /// 
        /// This ID is generated when a call is initiated and is used throughout the call lifecycle to:
        /// - Associate WebSocket connections with specific calls for real-time audio streaming
        /// - Link transcription events to the correct call context
        /// - Enable proper cleanup of call-specific resources when the call ends
        /// - Coordinate between different components (call automation, transcription, bot communication)
        /// 
        /// The correlation ID is typically passed in WebSocket headers (x-ms-call-correlation-id)
        /// and used as a key in the call store for quick lookup of call-related information.
        /// </summary>
        public string? CorrelationId { get; set; }

        /// <summary>
        /// The DirectLine conversation identifier that represents the bot conversation session.
        /// 
        /// This ID is obtained when starting a new conversation with the Bot Framework via DirectLine API
        /// and is used to:
        /// - Send user messages (transcribed speech) to the correct bot conversation
        /// - Receive bot responses through the DirectLine WebSocket stream
        /// - Maintain conversation state and context throughout the call
        /// - Enable proper message routing in multi-user scenarios
        /// 
        /// The conversation ID remains active for the duration of the call and is used to ensure
        /// that all speech-to-text results are sent to the same bot conversation, maintaining
        /// conversational continuity and context.
        /// </summary>
        public string? ConversationId { get; set; }

        /// <summary>
        /// The phone number or identifier of the caller initiating the call.
        /// 
        /// This information is extracted from the EventGrid IncomingCall event and can be:
        /// - A PSTN phone number in E.164 format (e.g., "+1234567890")
        /// - An Azure Communication Services user identifier
        /// - A raw identifier from the communication platform
        /// - "Unknown" if caller information is not available or blocked
        /// 
        /// Used for personalizing the conversation experience and caller identification.
        /// </summary>
        public string? CallerId { get; set; }

        /// <summary>
        /// The phone number or identifier that was called (the destination number).
        /// 
        /// This represents the number the caller dialed to reach the service and can be used for:
        /// - Department routing and context determination
        /// - Service-specific greeting customization
        /// - Call flow routing based on the called number
        /// - Analytics and reporting on which numbers receive calls
        /// 
        /// Extracted from the EventGrid IncomingCall event data.
        /// </summary>
        public string? CalleeId { get; set; }

        /// <summary>
        /// The display name associated with the caller's phone number or identifier.
        /// 
        /// This is typically the name that appears on caller ID displays and can be:
        /// - The registered name for a phone number
        /// - A user-friendly name for ACS identities
        /// - "Anonymous Caller" if no display name is available
        /// - Custom names set by the calling party
        /// 
        /// Used to provide personalized greetings and improve the customer experience.
        /// </summary>
        public string? CallerDisplayName { get; set; }

        /// <summary>
        /// Indicates the type of caller identification available for this call.
        /// 
        /// Possible values:
        /// - "pstn": Traditional phone number call
        /// - "acs": Azure Communication Services user
        /// - "raw": Raw identifier format
        /// - "unknown": Unable to determine caller type
        /// 
        /// Used for handling different types of callers appropriately.
        /// </summary>
        public string CallerType { get; set; } = "unknown";

        /// <summary>
        /// Indicates whether reliable caller identification information is available.
        /// 
        /// True when caller ID, display name, or other identifying information is present
        /// and can be used for personalization. False when the call is anonymous, blocked,
        /// or technical issues prevent caller identification.
        /// </summary>
        public bool HasCallerInfo { get; set; } = false;

        /// <summary>
        /// Indicates whether reliable callee (destination) information is available.
        /// 
        /// True when the destination number information is present and valid.
        /// Used to determine if department-specific routing or greetings can be applied.
        /// </summary>
        public bool HasCalleeInfo { get; set; } = false;

        /// <summary>
        /// The timestamp when the call was initiated or when this context was created.
        /// 
        /// Used for:
        /// - Call duration calculations
        /// - Session timeout management
        /// - Analytics and reporting
        /// - Debugging and troubleshooting
        /// </summary>
        public DateTimeOffset CallStartTime { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// The timestamp of the last activity or update for this call context.
        /// 
        /// Updated when messages are sent, responses are received, or other call events occur.
        /// Used for session management and cleanup of inactive calls.
        /// </summary>
        public DateTimeOffset? LastActivity { get; set; }

        /// <summary>
        /// Indicates whether this call context is currently active and handling a live call.
        /// 
        /// Set to false when the call ends to prevent further processing but maintain
        /// the context for cleanup and final operations.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Counter for the number of messages processed in this call conversation.
        /// 
        /// Used for analytics, billing, rate limiting, and debugging purposes.
        /// Incremented each time a message is sent to or received from the bot.
        /// </summary>
        public int MessageCount { get; set; } = 0;

        /// <summary>
        /// Thread synchronization object for safe concurrent access to this call context.
        /// 
        /// Used to ensure thread-safe updates to properties when multiple operations
        /// might be modifying the same call context simultaneously.
        /// </summary>
        public readonly object Lock = new object();

        /// <summary>
        /// Updates the LastActivity timestamp and increments the message count in a thread-safe manner.
        /// 
        /// Call this method whenever a message is sent or received for this call context
        /// to maintain accurate activity tracking and session state.
        /// </summary>
        public void UpdateLastActivity()
        {
            lock (Lock)
            {
                LastActivity = DateTimeOffset.UtcNow;
                MessageCount++;
            }
        }

        /// <summary>
        /// Gets a summary of the call context data quality and completeness.
        /// 
        /// Returns:
        /// - "complete": Both caller and callee information available
        /// - "partial": Either caller or callee information available
        /// - "minimal": No caller/callee information available
        /// 
        /// Used for analytics and determining the level of personalization possible.
        /// </summary>
        public string GetDataQuality()
        {
            if (HasCallerInfo && HasCalleeInfo)
                return "complete";
            if (HasCallerInfo || HasCalleeInfo)
                return "partial";
            return "minimal";
        }

        /// <summary>
        /// Creates a thread-safe snapshot of the caller information for safe access.
        /// 
        /// Returns a copy of the caller-related fields that can be safely used
        /// without holding locks, preventing deadlocks in multi-threaded scenarios.
        /// </summary>
        public (string CallerId, string CalleeId, string CallerDisplayName, bool HasCallerInfo, bool HasCalleeInfo, string CallerType) GetCallerInfoSnapshot()
        {
            lock (Lock)
            {
                return (
                    CallerId ?? "Unknown",
                    CalleeId ?? "Unknown", 
                    CallerDisplayName ?? "Anonymous Caller",
                    HasCallerInfo,
                    HasCalleeInfo,
                    CallerType
                );
            }
        }
    }
}
