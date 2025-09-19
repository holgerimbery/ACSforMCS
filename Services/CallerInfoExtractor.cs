using Azure.Messaging.EventGrid;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ACSforMCS.Services
{
    /// <summary>
    /// Utility class for extracting caller and callee information from Azure Communication Services EventGrid events.
    /// Provides safe, robust parsing of incoming call data with comprehensive error handling and fallback mechanisms.
    /// </summary>
    public static class CallerInfoExtractor
    {
        /// <summary>
        /// Enumeration of possible caller identification statuses for categorizing different scenarios.
        /// </summary>
        public enum CallerIdStatus
        {
            /// <summary>Full phone number and display name available</summary>
            Available,
            /// <summary>Some identifier present but not standard phone format</summary>
            PartiallyAvailable,
            /// <summary>Caller intentionally blocked their ID</summary>
            Blocked,
            /// <summary>Technical issue prevented ID retrieval</summary>
            Unavailable,
            /// <summary>International number with potential format issues</summary>
            International,
            /// <summary>Internal Azure Communication Services user</summary>
            Internal
        }

        /// <summary>
        /// Data transfer object containing extracted caller information and metadata.
        /// </summary>
        public class CallerInfo
        {
            public string CorrelationId { get; set; } = string.Empty;
            public string CallerId { get; set; } = "Unknown";
            public string CalleeId { get; set; } = "Unknown";
            public string CallerDisplayName { get; set; } = "Anonymous Caller";
            public string CallerType { get; set; } = "unknown";
            public bool HasCallerInfo { get; set; } = false;
            public bool HasCalleeInfo { get; set; } = false;
            public CallerIdStatus Status { get; set; } = CallerIdStatus.Unavailable;
            public DateTimeOffset ExtractedAt { get; set; } = DateTimeOffset.UtcNow;
            public DateTimeOffset CallStartTime { get; set; } = DateTimeOffset.UtcNow;
            public string DataQuality => GetDataQuality();

            /// <summary>
            /// Creates a default CallerInfo instance for error scenarios.
            /// </summary>
            public static CallerInfo CreateDefault(string correlationId)
            {
                return new CallerInfo
                {
                    CorrelationId = correlationId,
                    CallerId = $"Unknown-{correlationId[^8..]}",
                    CalleeId = "Unknown",
                    CallerDisplayName = "Anonymous Caller",
                    CallerType = "unknown",
                    HasCallerInfo = false,
                    HasCalleeInfo = false,
                    Status = CallerIdStatus.Unavailable
                };
            }

            /// <summary>
            /// Determines the overall data quality based on available information.
            /// </summary>
            private string GetDataQuality()
            {
                if (HasCallerInfo && HasCalleeInfo && Status == CallerIdStatus.Available)
                    return "complete";
                if (HasCallerInfo || HasCalleeInfo)
                    return "partial";
                return "minimal";
            }

            /// <summary>
            /// Applies the caller information to a CallContext instance.
            /// </summary>
            public void ApplyToCallContext(CallContext callContext)
            {
                if (callContext == null) return;

                lock (callContext.Lock)
                {
                    callContext.CallerId = CallerId;
                    callContext.CalleeId = CalleeId;
                    callContext.CallerDisplayName = CallerDisplayName;
                    callContext.CallerType = CallerType;
                    callContext.HasCallerInfo = HasCallerInfo;
                    callContext.HasCalleeInfo = HasCalleeInfo;
                    callContext.CallStartTime = CallStartTime;
                }
            }
        }

        /// <summary>
        /// Extracts caller and callee information from an Azure EventGrid event with comprehensive error handling.
        /// </summary>
        /// <param name="eventData">The EventGrid event containing call information</param>
        /// <param name="correlationId">Unique identifier for this call</param>
        /// <param name="logger">Logger instance for tracking extraction results</param>
        /// <returns>CallerInfo object with extracted data or safe defaults</returns>
        public static CallerInfo ExtractCallerInfo(EventGridEvent eventData, string correlationId, ILogger logger)
        {
            if (eventData == null)
            {
                logger.LogWarning("EventGrid event is null for call {CorrelationId}", correlationId);
                return CallerInfo.CreateDefault(correlationId);
            }

            try
            {
                var jsonNode = JsonNode.Parse(eventData.Data);
                var jsonObject = jsonNode?.AsObject();
                
                if (jsonObject == null)
                {
                    logger.LogWarning("Failed to parse event data as JSON for call {CorrelationId}", correlationId);
                    return CallerInfo.CreateDefault(correlationId);
                }

                var callerInfo = new CallerInfo
                {
                    CorrelationId = correlationId,
                    ExtractedAt = DateTimeOffset.UtcNow,
                    CallStartTime = DateTimeOffset.UtcNow
                };

                // Extract caller information (from field)
                ExtractCallerData(jsonObject, callerInfo, logger, correlationId);
                
                // Extract callee information (to field)
                ExtractCalleeData(jsonObject, callerInfo, logger, correlationId);
                
                // Extract caller display name
                ExtractCallerDisplayName(jsonObject, callerInfo, logger, correlationId);

                // Determine overall status and quality
                DetermineCallerStatus(callerInfo);

                logger.LogInformation("Extracted caller info for call {CorrelationId}: Status={Status}, CallerId={CallerId}, CalleeId={CalleeId}, Quality={Quality}", 
                    correlationId, callerInfo.Status, callerInfo.CallerId, callerInfo.CalleeId, callerInfo.DataQuality);

                return callerInfo;
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "JSON parsing error extracting caller info for call {CorrelationId}: {Message}", correlationId, ex.Message);
                return CallerInfo.CreateDefault(correlationId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error extracting caller info for call {CorrelationId}: {Message}", correlationId, ex.Message);
                return CallerInfo.CreateDefault(correlationId);
            }
        }

        /// <summary>
        /// Extracts caller identification from the 'from' field using multiple extraction strategies.
        /// </summary>
        private static void ExtractCallerData(JsonObject jsonObject, CallerInfo callerInfo, ILogger logger, string correlationId)
        {
            var fromData = jsonObject["from"];
            if (fromData == null)
            {
                logger.LogDebug("No 'from' field found in event data for call {CorrelationId}", correlationId);
                return;
            }

            // Define extraction strategies in order of preference
            var extractors = new (Func<JsonNode?, string?> Extractor, string Type)[]
            {
                (node => node?["phoneNumber"]?["value"]?.ToString(), "pstn"),
                (node => node?["communicationUser"]?["id"]?.ToString(), "acs"),
                (node => node?["rawId"]?.ToString(), "raw")
            };

            foreach (var (extractor, type) in extractors)
            {
                try
                {
                    var result = extractor(fromData);
                    if (!string.IsNullOrEmpty(result))
                    {
                        callerInfo.CallerId = result;
                        callerInfo.CallerType = type;
                        callerInfo.HasCallerInfo = true;
                        
                        logger.LogDebug("Extracted caller ID using {Type} method for call {CorrelationId}: {CallerId}", 
                            type, correlationId, result);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Caller ID extraction method {Type} failed for call {CorrelationId}", type, correlationId);
                }
            }

            logger.LogWarning("No caller ID found using any extraction method for call {CorrelationId}", correlationId);
            callerInfo.CallerId = $"Unknown-{correlationId[^8..]}";
        }

        /// <summary>
        /// Extracts callee identification from the 'to' field using multiple extraction strategies.
        /// </summary>
        private static void ExtractCalleeData(JsonObject jsonObject, CallerInfo callerInfo, ILogger logger, string correlationId)
        {
            var toData = jsonObject["to"];
            if (toData == null)
            {
                logger.LogDebug("No 'to' field found in event data for call {CorrelationId}", correlationId);
                return;
            }

            // Define extraction strategies in order of preference
            var extractors = new Func<JsonNode?, string?>[]
            {
                node => node?["phoneNumber"]?["value"]?.ToString(),
                node => node?["communicationUser"]?["id"]?.ToString(),
                node => node?["rawId"]?.ToString()
            };

            foreach (var extractor in extractors)
            {
                try
                {
                    var result = extractor(toData);
                    if (!string.IsNullOrEmpty(result))
                    {
                        callerInfo.CalleeId = result;
                        callerInfo.HasCalleeInfo = true;
                        
                        logger.LogDebug("Extracted callee ID for call {CorrelationId}: {CalleeId}", correlationId, result);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Callee ID extraction method failed for call {CorrelationId}", correlationId);
                }
            }

            logger.LogWarning("No callee ID found using any extraction method for call {CorrelationId}", correlationId);
            callerInfo.CalleeId = "Unknown";
        }

        /// <summary>
        /// Extracts caller display name from the event data.
        /// </summary>
        private static void ExtractCallerDisplayName(JsonObject jsonObject, CallerInfo callerInfo, ILogger logger, string correlationId)
        {
            try
            {
                var displayName = jsonObject["callerDisplayName"]?.ToString();
                if (!string.IsNullOrEmpty(displayName))
                {
                    callerInfo.CallerDisplayName = displayName;
                    logger.LogDebug("Extracted caller display name for call {CorrelationId}: {DisplayName}", correlationId, displayName);
                }
                else
                {
                    logger.LogDebug("No caller display name found for call {CorrelationId}", correlationId);
                    
                    // Generate a friendly name based on caller ID if available
                    if (callerInfo.HasCallerInfo && callerInfo.CallerId != "Unknown")
                    {
                        callerInfo.CallerDisplayName = GenerateFriendlyCallerName(callerInfo.CallerId, callerInfo.CallerType);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error extracting caller display name for call {CorrelationId}", correlationId);
                callerInfo.CallerDisplayName = "Anonymous Caller";
            }
        }

        /// <summary>
        /// Determines the overall caller identification status based on available data.
        /// </summary>
        private static void DetermineCallerStatus(CallerInfo callerInfo)
        {
            if (!callerInfo.HasCallerInfo)
            {
                callerInfo.Status = CallerIdStatus.Unavailable;
                return;
            }

            // Check for blocked/private numbers
            if (callerInfo.CallerId.Contains("Private", StringComparison.OrdinalIgnoreCase) ||
                callerInfo.CallerId.Contains("Blocked", StringComparison.OrdinalIgnoreCase) ||
                callerInfo.CallerId.Contains("Anonymous", StringComparison.OrdinalIgnoreCase))
            {
                callerInfo.Status = CallerIdStatus.Blocked;
                return;
            }

            // Check for valid phone number format
            if (callerInfo.CallerType == "pstn" && callerInfo.CallerId.StartsWith("+"))
            {
                // International number check
                if (callerInfo.CallerId.Length > 15) // E.164 max length
                {
                    callerInfo.Status = CallerIdStatus.International;
                }
                else
                {
                    callerInfo.Status = CallerIdStatus.Available;
                }
                return;
            }

            // Check for ACS internal users
            if (callerInfo.CallerType == "acs")
            {
                callerInfo.Status = CallerIdStatus.Internal;
                return;
            }

            // Partial information available
            callerInfo.Status = CallerIdStatus.PartiallyAvailable;
        }

        /// <summary>
        /// Generates a friendly caller name when display name is not available.
        /// </summary>
        private static string GenerateFriendlyCallerName(string callerId, string callerType)
        {
            return callerType switch
            {
                "pstn" when callerId.StartsWith("+") => $"Caller {callerId}",
                "acs" => "Communication User",
                "raw" => "System User",
                _ => "Anonymous Caller"
            };
        }

        /// <summary>
        /// Validates and normalizes a phone number to E.164 format if possible.
        /// </summary>
        public static string? NormalizePhoneNumber(string? phoneNumber)
        {
            if (string.IsNullOrEmpty(phoneNumber))
                return null;

            // Remove common formatting characters
            var cleaned = phoneNumber.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "");
            
            // Ensure it starts with +
            if (!cleaned.StartsWith("+"))
            {
                // Assume US number if it starts with 1 and is 11 digits
                if (cleaned.StartsWith("1") && cleaned.Length == 11)
                {
                    cleaned = "+" + cleaned;
                }
                // Assume US number if 10 digits
                else if (cleaned.Length == 10 && cleaned.All(char.IsDigit))
                {
                    cleaned = "+1" + cleaned;
                }
                else
                {
                    return phoneNumber; // Return original if we can't normalize
                }
            }

            return cleaned;
        }

        /// <summary>
        /// Gets a user-friendly description of the caller status for logging and debugging.
        /// </summary>
        public static string GetStatusDescription(CallerIdStatus status)
        {
            return status switch
            {
                CallerIdStatus.Available => "Full caller identification available",
                CallerIdStatus.PartiallyAvailable => "Limited caller identification available",
                CallerIdStatus.Blocked => "Caller ID intentionally blocked or private",
                CallerIdStatus.Unavailable => "No caller identification available",
                CallerIdStatus.International => "International caller with potential format issues",
                CallerIdStatus.Internal => "Internal Azure Communication Services user",
                _ => "Unknown caller identification status"
            };
        }
    }
}