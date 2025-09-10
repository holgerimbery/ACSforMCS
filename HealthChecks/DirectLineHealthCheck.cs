using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ACSforMCS.Configuration;
using System.Net.Http.Headers;

namespace ACSforMCS.HealthChecks
{
    /// <summary>
    /// Health check implementation for monitoring the availability and responsiveness of the DirectLine service.
    /// DirectLine is Microsoft's API for connecting applications to Bot Framework bots.
    /// This health check verifies that the DirectLine service is accessible and responding correctly.
    /// </summary>
    public class DirectLineHealthCheck : IHealthCheck
    {
        /// <summary>
        /// The DirectLine secret used for authenticating with the Bot Framework DirectLine API.
        /// This secret is obtained from the Bot Framework registration and allows secure communication.
        /// </summary>
        private readonly string _directLineSecret;

        /// <summary>
        /// Initializes a new instance of the DirectLineHealthCheck with the required DirectLine secret.
        /// </summary>
        /// <param name="settings">Application settings containing the DirectLine secret configuration</param>
        /// <exception cref="ArgumentNullException">Thrown when the DirectLine secret is null or empty</exception>
        public DirectLineHealthCheck(IOptions<AppSettings> settings)
        {
            // Ensure the DirectLine secret is provided and not null
            _directLineSecret = settings.Value.DirectLineSecret ?? throw new ArgumentNullException(nameof(settings.Value.DirectLineSecret));
        }

        /// <summary>
        /// Performs the health check by attempting to start a conversation with the DirectLine service.
        /// This method tests connectivity and authentication with the Bot Framework DirectLine API.
        /// </summary>
        /// <param name="context">Health check context containing additional information about the check</param>
        /// <param name="cancellationToken">Cancellation token to support operation cancellation</param>
        /// <returns>
        /// A HealthCheckResult indicating:
        /// - Healthy: DirectLine service is responding correctly
        /// - Degraded: DirectLine service responded but with an error status code
        /// - Unhealthy: DirectLine service is unreachable or threw an exception
        /// </returns>
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, 
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Create HTTP client for making the health check request
                using var httpClient = new HttpClient();
                
                // Set up Bearer token authentication using the DirectLine secret
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _directLineSecret);
                
                // Test DirectLine service by attempting to start a conversation
                // This is the correct DirectLine API endpoint that validates authentication
                var response = await httpClient.PostAsync(
                    "https://directline.botframework.com/v3/directline/conversations", // Correct DirectLine endpoint
                    new StringContent("", System.Text.Encoding.UTF8, "application/json"),
                    cancellationToken);

                // Check if the response indicates success (2xx status codes)
                if (response.IsSuccessStatusCode)
                {
                    return HealthCheckResult.Healthy("DirectLine service is available and authenticated");
                }
                
                // Service responded but with an error status code - mark as degraded
                return HealthCheckResult.Degraded($"DirectLine service responded with status code: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                // Any exception (network issues, timeouts, etc.) indicates the service is unhealthy
                return HealthCheckResult.Unhealthy("DirectLine service is unavailable", ex);
            }
        }
    }
}
