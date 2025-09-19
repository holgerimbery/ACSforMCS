using Azure.Communication.CallAutomation;
using Microsoft.Extensions.Logging;

namespace ACSforMCS.Services
{
    /// <summary>
    /// Utility class for extracting and processing DTMF (Dual-Tone Multi-Frequency) data from Azure Communication Services events.
    /// Provides safe, robust processing of DTMF input with comprehensive error handling and validation.
    /// </summary>
    public static class DtmfExtractor
    {
        /// <summary>
        /// Enumeration of possible DTMF recognition statuses for categorizing different scenarios.
        /// </summary>
        public enum DtmfStatus
        {
            /// <summary>Valid DTMF tone recognized successfully</summary>
            Recognized,
            /// <summary>Invalid or unrecognized DTMF tone</summary>
            Invalid,
            /// <summary>DTMF recognition is not available for this call</summary>
            Unavailable,
            /// <summary>Technical issue prevented DTMF recognition</summary>
            Error
        }

        /// <summary>
        /// Data transfer object containing extracted DTMF information and metadata.
        /// </summary>
        public class DtmfInfo
        {
            public string CorrelationId { get; set; } = string.Empty;
            public string DtmfTone { get; set; } = string.Empty;
            public DtmfStatus Status { get; set; } = DtmfStatus.Unavailable;
            public DateTimeOffset RecognizedAt { get; set; } = DateTimeOffset.UtcNow;
            public string? ErrorMessage { get; set; }
            public bool IsValid => Status == DtmfStatus.Recognized && !string.IsNullOrEmpty(DtmfTone);

            /// <summary>
            /// Creates a default DtmfInfo instance for error scenarios.
            /// </summary>
            public static DtmfInfo CreateError(string correlationId, string errorMessage)
            {
                return new DtmfInfo
                {
                    CorrelationId = correlationId,
                    Status = DtmfStatus.Error,
                    ErrorMessage = errorMessage,
                    RecognizedAt = DateTimeOffset.UtcNow
                };
            }

            /// <summary>
            /// Creates a successful DtmfInfo instance for recognized tones.
            /// </summary>
            public static DtmfInfo CreateRecognized(string correlationId, string dtmfTone)
            {
                return new DtmfInfo
                {
                    CorrelationId = correlationId,
                    DtmfTone = dtmfTone,
                    Status = DtmfStatus.Recognized,
                    RecognizedAt = DateTimeOffset.UtcNow
                };
            }

            /// <summary>
            /// Applies the DTMF information to a CallContext instance.
            /// </summary>
            public void ApplyToCallContext(CallContext callContext)
            {
                if (callContext == null || !IsValid) return;

                callContext.AddDtmfTone(DtmfTone);
            }
        }

        /// <summary>
        /// Extracts DTMF information from a RecognizeCompleted event with comprehensive error handling.
        /// </summary>
        /// <param name="recognizeEvent">The RecognizeCompleted event containing DTMF data</param>
        /// <param name="correlationId">Unique identifier for this call</param>
        /// <param name="logger">Logger instance for tracking extraction results</param>
        /// <returns>DtmfInfo object with extracted data or safe defaults</returns>
        public static DtmfInfo ExtractDtmfFromRecognizeEvent(RecognizeCompleted recognizeEvent, string correlationId, ILogger logger)
        {
            if (recognizeEvent == null)
            {
                logger.LogWarning("RecognizeCompleted event is null for call {CorrelationId}", correlationId);
                return DtmfInfo.CreateError(correlationId, "Null RecognizeCompleted event");
            }

            try
            {
                var recognizeResult = recognizeEvent.RecognizeResult;
                
                if (recognizeResult == null)
                {
                    logger.LogWarning("RecognizeResult is null for call {CorrelationId}", correlationId);
                    return DtmfInfo.CreateError(correlationId, "Null RecognizeResult");
                }

                // Check if this is a DTMF recognition result
                if (recognizeResult is DtmfResult dtmfResult)
                {
                    return ProcessDtmfResult(dtmfResult, correlationId, logger);
                }

                // If it's not a DTMF result, it might be speech or other recognition
                logger.LogDebug("RecognizeCompleted event is not a DTMF result for call {CorrelationId}, type: {ResultType}", 
                    correlationId, recognizeResult.GetType().Name);
                
                return DtmfInfo.CreateError(correlationId, $"Not a DTMF result: {recognizeResult.GetType().Name}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error extracting DTMF from RecognizeCompleted event for call {CorrelationId}: {Message}", 
                    correlationId, ex.Message);
                return DtmfInfo.CreateError(correlationId, ex.Message);
            }
        }

        /// <summary>
        /// Processes a DtmfResult to extract individual DTMF tones.
        /// </summary>
        private static DtmfInfo ProcessDtmfResult(DtmfResult dtmfResult, string correlationId, ILogger logger)
        {
            try
            {
                if (dtmfResult.Tones == null || dtmfResult.Tones.Count == 0)
                {
                    logger.LogDebug("No DTMF tones found in result for call {CorrelationId}", correlationId);
                    return DtmfInfo.CreateError(correlationId, "No DTMF tones in result");
                }

                // Process the first tone (typically DTMF events contain one tone at a time)
                var firstTone = dtmfResult.Tones[0];
                var toneString = ConvertDtmfToneToString(firstTone);

                if (string.IsNullOrEmpty(toneString))
                {
                    logger.LogWarning("Unknown DTMF tone detected for call {CorrelationId}: {Tone}", correlationId, firstTone);
                    return DtmfInfo.CreateError(correlationId, $"Unknown DTMF tone: {firstTone}");
                }

                logger.LogInformation("DTMF tone recognized for call {CorrelationId}: {Tone} (Total tones in result: {ToneCount})", 
                    correlationId, toneString, dtmfResult.Tones.Count);

                return DtmfInfo.CreateRecognized(correlationId, toneString);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing DTMF result for call {CorrelationId}: {Message}", correlationId, ex.Message);
                return DtmfInfo.CreateError(correlationId, ex.Message);
            }
        }

        /// <summary>
        /// Converts a DtmfTone enum value to its string representation.
        /// </summary>
        private static string? ConvertDtmfToneToString(DtmfTone tone)
        {
            if (tone == DtmfTone.Zero) return "0";
            if (tone == DtmfTone.One) return "1";
            if (tone == DtmfTone.Two) return "2";
            if (tone == DtmfTone.Three) return "3";
            if (tone == DtmfTone.Four) return "4";
            if (tone == DtmfTone.Five) return "5";
            if (tone == DtmfTone.Six) return "6";
            if (tone == DtmfTone.Seven) return "7";
            if (tone == DtmfTone.Eight) return "8";
            if (tone == DtmfTone.Nine) return "9";
            if (tone == DtmfTone.Pound) return "#";
            if (tone == DtmfTone.Asterisk) return "*";
            return null;
        }

        /// <summary>
        /// Validates a DTMF sequence for completeness and format.
        /// </summary>
        /// <param name="dtmfSequence">The DTMF sequence to validate</param>
        /// <returns>True if the sequence contains only valid DTMF characters</returns>
        public static bool IsValidDtmfSequence(string? dtmfSequence)
        {
            if (string.IsNullOrEmpty(dtmfSequence))
                return false;

            // Check if all characters are valid DTMF characters (0-9, *, #)
            return dtmfSequence.All(c => char.IsDigit(c) || c == '*' || c == '#');
        }

        /// <summary>
        /// Gets a user-friendly description of the DTMF status for logging and debugging.
        /// </summary>
        public static string GetStatusDescription(DtmfStatus status)
        {
            return status switch
            {
                DtmfStatus.Recognized => "DTMF tone recognized successfully",
                DtmfStatus.Invalid => "Invalid or unrecognized DTMF tone",
                DtmfStatus.Unavailable => "DTMF recognition not available",
                DtmfStatus.Error => "Technical error during DTMF recognition",
                _ => "Unknown DTMF recognition status"
            };
        }

        /// <summary>
        /// Formats a DTMF sequence for user-friendly display.
        /// </summary>
        /// <param name="dtmfSequence">The raw DTMF sequence</param>
        /// <returns>Formatted string for display purposes</returns>
        public static string FormatDtmfSequence(string? dtmfSequence)
        {
            if (string.IsNullOrEmpty(dtmfSequence))
                return "No DTMF input";

            // Add spaces between digits for readability
            return string.Join(" ", dtmfSequence.ToCharArray());
        }
    }
}