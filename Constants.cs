namespace ACSforMCS
{
    /// <summary>
    /// Contains application-wide constants used throughout the Azure Communication Services 
    /// for Microsoft Customer Service (ACSforMCS) application. These constants define
    /// standard values for API endpoints, activity types, and configuration parameters
    /// that ensure consistency across different components of the system.
    /// </summary>
    public static class Constants
    {
        /// <summary>
        /// The WebSocket endpoint path for real-time audio streaming from Azure Communication Services.
        /// 
        /// This path is used by:
        /// - WebSocketMiddleware to identify incoming WebSocket upgrade requests
        /// - ACS to establish connections for streaming transcribed audio data
        /// - The system to route real-time speech-to-text results to the appropriate handlers
        /// 
        /// The full WebSocket URL is constructed by combining the base application URI with this path.
        /// </summary>
        public const string WebSocketPath = "/ws";

        /// <summary>
        /// The base URL for Microsoft's DirectLine API in the Europe region.
        /// 
        /// DirectLine is the REST API that enables communication with Bot Framework bots.
        /// The Europe-specific endpoint is used for:
        /// - Data residency compliance (keeping data within European boundaries)
        /// - Reduced latency for European deployments
        /// - Regional service availability and reliability
        /// 
        /// This URL is used to start conversations, send messages, and obtain WebSocket stream URLs
        /// for real-time bot communication.
        /// Use standard endpoint for non-European regions.
        /// public const string DirectLineBaseUrl = "https://directline.botframework.com/v3/directline/";
        /// </summary>
        public const string DirectLineBaseUrl = "https://europe.directline.botframework.com/v3/directline/";
                /// <summary>
        /// Default user identifier used in Bot Framework activities to represent the caller.
        /// 
        /// This identifier is used when:
        /// - Sending transcribed speech as user messages to the bot
        /// - Distinguishing between user messages and bot responses in activity streams
        /// - Maintaining consistent user identity throughout the conversation
        /// 
        /// The bot can use this ID to personalize responses or maintain user-specific context.
        /// </summary>
        public const string DefaultUserName = "user1";

        /// <summary>
        /// Bot Framework activity type identifier for regular text/speech messages.
        /// 
        /// This constant represents the standard Bot Framework message activity type and is used to:
        /// - Identify bot responses that should be converted to speech and played to the caller
        /// - Filter activity streams to extract only conversational content
        /// - Distinguish regular messages from control activities (transfers, end-of-conversation, etc.)
        /// 
        /// Messages with this type typically contain text or SSML content for speech synthesis.
        /// </summary>
        public const string MessageActivityType = "message";

        /// <summary>
        /// Bot Framework activity type identifier for conversation termination signals.
        /// 
        /// When the bot sends an activity with this type, it indicates that:
        /// - The conversation has reached a natural conclusion
        /// - The call should be terminated gracefully
        /// - No further user input is expected
        /// - The system should hang up the call and clean up resources
        /// 
        /// This provides a way for the bot to programmatically end conversations when appropriate.
        /// </summary>
        public const string EndOfConversationActivityType = "endOfConversation";

        /// <summary>
        /// Custom activity type identifier for error responses from the bot.
        /// 
        /// This type is used internally to categorize bot responses that indicate errors or failures.
        /// Error activities are typically:
        /// - Not played directly to the caller (to avoid confusing technical messages)
        /// - Logged for debugging and monitoring purposes
        /// - Handled with user-friendly fallback responses
        /// - Used to trigger alternative conversation flows or escalation
        /// 
        /// The system may convert these into more appropriate user-facing messages.
        /// </summary>
        public const string ErrorActivityType = "Error";

        /// <summary>
        /// Custom activity type identifier for call transfer commands from the bot.
        /// 
        /// When the bot determines that a call should be transferred to a human agent or another number,
        /// it sends an activity with this type. Transfer activities typically contain:
        /// - The target phone number in E.164 format
        /// - An optional transfer message to announce to the caller
        /// - Instructions for the type of transfer (warm, cold, supervised)
        /// 
        /// The CallAutomationService processes these activities to initiate call transfers
        /// using Azure Communication Services transfer capabilities.
        /// </summary>
        public const string TransferActivityType = "transfer";

        /// <summary>
        /// Default operation context identifier used in Azure Communication Services operations.
        /// 
        /// This context string is included in ACS API calls to:
        /// - Help identify operations in logs and monitoring systems
        /// - Group related operations for debugging and analytics
        /// - Provide context for support and troubleshooting scenarios
        /// - Enable filtering and correlation of ACS events
        /// 
        /// In production, this might be replaced with more specific context values
        /// like call IDs, session identifiers, or environment indicators.
        /// </summary>
        public const string DefaultOperationContext = "Testing";
    }
}
