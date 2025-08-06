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
    }
}
