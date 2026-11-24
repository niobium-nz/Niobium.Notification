using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Niobium.Notification.Delivery;
using Niobium.Platform.Notification.Email.Resend;

namespace Niobium.Notification.Server.Delivery
{
    public sealed class ResendSubmissionProvider(HttpClient client, IOptions<ResendServiceOptions> options, TimeProvider clock) : IIdempotentNotificationProvider
    {
        public FrozenNotification Freeze(TemplateDomain.Deliverable content, NotificationCorrelation? correlation = null)
        {
            string domain = new MailAddress(content.From).Host;
            string credential = this.Credential(domain);
            if (String.IsNullOrWhiteSpace(credential))
            {
                throw new InvalidOperationException("Notification provider credentials are not configured.");
            }
            Dictionary<string, object?> fields = new()
            {
                ["from"] = new MailAddress(content.From, content.FromName).ToString(),
                ["to"] = new[] { new MailAddress(content.To, content.ToName).ToString() },
                ["subject"] = content.Subject,
                ["html"] = content.Body,
            };
            if (correlation != null)
            {
                fields["tags"] = new[]
                {
                    new { name = "niobium_tenant", value = correlation.Tenant },
                    new { name = "niobium_submission", value = correlation.SubmissionID },
                    new { name = "niobium_proof", value = correlation.Proof },
                };
            }
            string json = JsonSerializer.Serialize(fields);
            return FrozenNotification.Create(json, domain, credential);
        }

        public async Task<SubmissionResult> SubmitAsync(FrozenNotification content, string key, DateTimeOffset deadline, CancellationToken cancellationToken)
        {
            string credential = this.Credential(content.SenderDomain);
            if (!content.MatchesCredential(credential)
                || clock.GetUtcNow() + NotificationRetryPolicy.SendMargin >= deadline)
            {
                return new(SubmissionOutcome.Hold);
            }
            using HttpRequestMessage request = new(HttpMethod.Post, "https://api.resend.com/emails");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            request.Headers.Add("Idempotency-Key", key);
            request.Content = new StringContent(content.Json, Encoding.UTF8, "application/json");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            HttpStatusCode? responseStatus = null;
            try
            {
                // No resilience handler or automatic retry: the durable flow owns every attempt.
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                responseStatus = response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using JsonDocument result = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
                    return result.RootElement.ValueKind == JsonValueKind.Object
                        && result.RootElement.TryGetProperty("id", out JsonElement id)
                        && id.ValueKind == JsonValueKind.String && !String.IsNullOrWhiteSpace(id.GetString())
                        ? new(SubmissionOutcome.Accepted, id.GetString()) : new(SubmissionOutcome.Retry);
                }
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using JsonDocument result = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
                    return result.RootElement.ValueKind == JsonValueKind.Object
                        && result.RootElement.TryGetProperty("name", out JsonElement name)
                        && name.ValueKind == JsonValueKind.String && name.GetString() == "concurrent_idempotent_requests"
                        ? new(SubmissionOutcome.Retry) : new(SubmissionOutcome.Hold);
                }
                return response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500
                    ? new(SubmissionOutcome.Retry) : new(SubmissionOutcome.Rejected);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return IncompleteResponse(responseStatus);
            }
            catch (HttpRequestException)
            {
                return IncompleteResponse(responseStatus);
            }
            catch (JsonException)
            {
                return IncompleteResponse(responseStatus);
            }
            catch (IOException)
            {
                return IncompleteResponse(responseStatus);
            }
        }

        // A known conflict is retryable only when its body positively identifies
        // concurrent requests. An unreadable body must not turn it into a retry.
        private static SubmissionResult IncompleteResponse(HttpStatusCode? status)
            => new(status == HttpStatusCode.Conflict ? SubmissionOutcome.Hold : SubmissionOutcome.Retry);

        private string Credential(string domain)
            => options.Value.DomainScopedAPIKeys != null && options.Value.DomainScopedAPIKeys.TryGetValue(domain, out string? key)
                ? key : options.Value.GlobalAPIKey ?? String.Empty;
    }
}
