using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ACSforMCS.Configuration;
using System.Net.Http.Headers;

namespace ACSforMCS.HealthChecks
{
    public class DirectLineHealthCheck : IHealthCheck
    {
        private readonly string _directLineSecret;

        public DirectLineHealthCheck(IOptions<AppSettings> settings)
        {
            _directLineSecret = settings.Value.DirectLineSecret ?? throw new ArgumentNullException(nameof(settings.Value.DirectLineSecret));
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, 
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _directLineSecret);
                
                var response = await httpClient.GetAsync(
                    "https://europe.directline.botframework.com/v3/directline/tokens/ping", 
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return HealthCheckResult.Healthy("DirectLine service is available");
                }
                
                return HealthCheckResult.Degraded($"DirectLine service responded with status code: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("DirectLine service is unavailable", ex);
            }
        }
    }
}