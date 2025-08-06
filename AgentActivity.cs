namespace ACSforMCS 
{
    /// <summary>
    /// Represents a structured activity received from a Bot Framework agent via DirectLine API.
    /// This class is used to parse and encapsulate different types of bot responses including
    /// regular messages, transfer commands, conversation control signals, and error responses.
    /// 
    /// The AgentActivity serves as a data transfer object that standardizes how bot responses
    /// are processed within the Azure Communication Services call automation workflow.
    /// </summary>
    public class AgentActivity
    {
        /// <summary>
        /// The type of activity received from the bot agent.
        /// 
        /// Common activity types include:
        /// - "message": Regular text or speech responses from the bot
        /// - "transfer": Commands to transfer the call to a human agent or another number
        /// - "endOfConversation": Signals that the conversation should be terminated
        /// - "error": Indicates an error occurred in bot processing
        /// 
        /// This property corresponds to the Bot Framework Activity.Type field and is used
        /// by the CallAutomationService to determine how to process the bot's response.
        /// </summary>
        public string? Type { get; set; }

        /// <summary>
        /// The content or payload of the activity from the bot agent.
        /// 
        /// The interpretation of this field depends on the Type:
        /// - For "message" activities: Contains the text to be spoken to the caller via text-to-speech
        /// - For "transfer" activities: Contains the phone number and optional transfer message (format: "phoneNumber|message")
        /// - For "error" activities: Contains the error message or description
        /// - For "endOfConversation" activities: Usually null or contains a farewell message
        /// 
        /// This text is often processed to remove references and citations before being converted
        /// to speech for the caller, ensuring a natural voice experience.
        /// </summary>
        public string? Text { get; set; }   
    }
}