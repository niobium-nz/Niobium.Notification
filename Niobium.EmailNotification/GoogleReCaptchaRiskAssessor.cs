using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Niobium.EmailNotification
{
    internal class GoogleReCaptchaRiskAssessor(
        HttpClient httpClient,
        IOptions<EmailNotificationOptions> options,
        ILogger<GoogleReCaptchaRiskAssessor> logger)
        : IVisitorRiskAssessor
    {
        private static readonly JsonSerializerOptions SERIALIZATION_OPTIONS = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        public async Task<bool> AssessAsync(Guid requestID, string tenant, string token, string? clientIP, CancellationToken cancellationToken)
        {
            var secret = options.Value.Secrets[tenant]
                ?? throw new ApplicationException($"Missing tenant secret: {tenant}");

            List<KeyValuePair<string, string>> parameters = new([
                new KeyValuePair<string, string>("secret", secret),
                new KeyValuePair<string, string>("response", token),
            ]);
            if (!string.IsNullOrWhiteSpace(clientIP))
            {
                parameters.Add(new KeyValuePair<string, string>("remoteip", clientIP));
            }
            var payload = new FormUrlEncodedContent(parameters);

            using var response = await httpClient.PostAsync("recaptcha/api/siteverify", payload, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError($"Error response {response.StatusCode} from Google ReCaptcha on request {requestID}.");
                return false;
            }

            var respbody = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = Deserialize<GoogleReCaptchaResult>(respbody);
            if (result == null)
            {
                logger.LogError($"Error deserializing Google ReCaptcha response: {respbody} on request {requestID}.");
                return false;
            }

            return result.Success && result.Hostname.ToLower() == tenant;
        }

        private static T Deserialize<T>(string json) => System.Text.Json.JsonSerializer.Deserialize<T>(json, SERIALIZATION_OPTIONS)!;

        class GoogleReCaptchaResult
        {
            public required bool Success { get; set; }

            public DateTimeOffset ChallengeTs { get; set; }

            public required string Hostname { get; set; }

            public required double Score { get; set; }

            public required string Action { get; set; }
        }
    }
}
