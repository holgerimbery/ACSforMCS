namespace ACSforMCS.Configuration
{
    public class AppSettings
    {
        public string AcsConnectionString { get; set; } = string.Empty;
        public string CognitiveServiceEndpoint { get; set; } = string.Empty;
        public string AgentPhoneNumber { get; set; } = string.Empty;
        public string DirectLineSecret { get; set; } = string.Empty;
        public string BaseUri { get; set; } = string.Empty;
        public string DefaultTransferNumber { get; set; } = string.Empty; // Add this for fallback transfers
    }

    public class VoiceOptions
    {
        public string VoiceName { get; set; } = "en-US-NancyNeural";
        public string Language { get; set; } = "en-US";
    }
}