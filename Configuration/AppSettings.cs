namespace ACSforMCS.Configuration
{
    /// <summary>
    /// Configuration settings for the Azure Communication Services (ACS) for Microsoft Customer Service application.
    /// These settings are typically loaded from appsettings.json or Azure Key Vault.
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// Connection string for Azure Communication Services.
        /// Used to authenticate and connect to ACS for call automation features.
        /// Format: endpoint=https://your-resource.communication.azure.com/;accesskey=your-access-key
        /// </summary>
        public string AcsConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Endpoint URL for Azure Cognitive Services.
        /// Used for speech-to-text, text-to-speech, and other AI capabilities.
        /// Format: https://your-region.api.cognitive.microsoft.com/
        /// </summary>
        public string CognitiveServiceEndpoint { get; set; } = string.Empty;

        /// <summary>
        /// Phone number assigned to the agent for call handling.
        /// This should be a phone number acquired through Azure Communication Services.
        /// Format: +1234567890 (E.164 format)
        /// </summary>
        public string AgentPhoneNumber { get; set; } = string.Empty;

        /// <summary>
        /// Secret key for DirectLine API communication with the bot framework.
        /// Used to establish secure communication between the application and the bot.
        /// This is obtained from the Bot Framework registration.
        /// </summary>
        public string DirectLineSecret { get; set; } = string.Empty;

        /// <summary>
        /// Base URI for the application.
        /// Used for webhook callbacks and API endpoints.
        /// Should include the protocol and domain (e.g., https://yourapp.azurewebsites.net)
        /// </summary>
        public string BaseUri { get; set; } = string.Empty;

        /// <summary>
        /// Default phone number to transfer calls to when the primary transfer fails or no specific transfer number is available.
        /// This serves as a fallback option to ensure calls are not dropped.
        /// Format: +1234567890 (E.164 format)
        /// </summary>
        public string DefaultTransferNumber { get; set; } = string.Empty; // Add this for fallback transfers
    }

    /// <summary>
    /// Configuration options for caller identification and personalization features.
    /// Controls how caller ID and callee ID information is handled and used throughout the application.
    /// </summary>
    public class CallerIdOptions
    {
        /// <summary>
        /// Enables or disables caller ID extraction and personalization features.
        /// When disabled, all calls are treated as anonymous with generic greetings.
        /// Default: true
        /// </summary>
        public bool EnableCallerIdProcessing { get; set; } = true;

        /// <summary>
        /// Enables detailed logging of caller ID extraction results for monitoring and debugging.
        /// When enabled, logs extraction success/failure rates and data quality metrics.
        /// Default: true
        /// </summary>
        public bool EnableDetailedLogging { get; set; } = true;
    }

    /// <summary>
    /// Configuration options for voice synthesis and speech recognition.
    /// Used to customize the voice characteristics for text-to-speech operations and optimize speech recognition timing.
    /// </summary>
    public class VoiceOptions
    {
        /// <summary>
        /// The specific voice to use for text-to-speech synthesis.
        /// This should be a valid Azure Cognitive Services voice name.
        /// Default: "en-US-NancyNeural" (English US, female neural voice)
        /// </summary>
        public string VoiceName { get; set; } = "en-US-NancyNeural";

        /// <summary>
        /// The language code for speech recognition and synthesis.
        /// Should match the language of the voice name selected.
        /// Format: language-region (e.g., "en-US", "es-ES", "fr-FR")
        /// Default: "en-US" (English United States)
        /// </summary>
        public string Language { get; set; } = "en-US";

        /// <summary>
        /// Initial silence timeout in milliseconds before speech recognition begins.
        /// Lower values make the system more responsive but may cut off slow speakers.
        /// Default: 800ms (0.8 seconds) - optimized for fastest response times
        /// </summary>
        public int InitialSilenceTimeoutMs { get; set; } = 800;

        /// <summary>
        /// End silence timeout in milliseconds to detect when user has finished speaking.
        /// Lower values make conversations faster but may cut off speakers with pauses.
        /// Default: 1500ms (1.5 seconds) - optimized for faster response times
        /// </summary>
        public int EndSilenceTimeoutMs { get; set; } = 1500;

        /// <summary>
        /// Maximum speech recognition duration in milliseconds.
        /// Prevents extremely long recognition sessions that could impact performance.
        /// Default: 30000ms (30 seconds)
        /// </summary>
        public int MaxRecognitionDurationMs { get; set; } = 30000;

        /// <summary>
        /// Enable fast mode speech recognition for quicker response times.
        /// When enabled, uses optimized speech recognition settings for rapid interactions.
        /// Default: true for better user experience
        /// </summary>
        public bool EnableFastMode { get; set; } = true;

        /// <summary>
        /// Voice speech rate for text-to-speech synthesis.
        /// Values: slow, medium, fast, x-fast, or percentage (e.g., "110%")
        /// Default: "fast" for quicker response times and better user experience
        /// </summary>
        public string SpeechRate { get; set; } = "fast";
    }
}