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
        public FrozenNotification Freeze(TemplateDomain.Deliverable content)
        {
            string domain = new MailAddress(content.From).Host;
            string credential = this.Credential(domain);
            if (String.IsNullOrWhiteSpace(credential))
            {
                throw new InvalidOperationException("Notification provider credentials are not configured.");
            }
            string json = JsonSerializer.Serialize(new
            {
                from = new MailAddress(content.From, content.FromName).ToString(),
                to = new[] { new MailAddress(content.To, content.ToName).ToString() },
                subject = content.Subject,
                html = content.Body,
            });
            return new FrozenNotification(json, domain, NotificationFingerprint.Hash(credential));
        }

        public async Task<SubmissionResult> SubmitAsync(FrozenNotification content, string key, DateTimeOffset deadline, CancellationToken cancellationToken)
        {
            string credential = this.Credential(content.SenderDomain);
            if (NotificationFingerprint.Hash(credential) != content.CredentialFingerprint
                || clock.GetUtcNow() + NotificationRetryPolicy.SendMargin >= deadline)
            {
                return new(SubmissionResult.Hold);
            }
            using HttpRequestMessage request = new(HttpMethod.Post, "https://api.resend.com/emails");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            request.Headers.Add("Idempotency-Key", key);
            request.Content = new StringContent(content.Json, Encoding.UTF8, "application/json");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                // No resilience handler or automatic retry: the durable flow owns every attempt.
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using JsonDocument result = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
                    return result.RootElement.TryGetProperty("id", out JsonElement id)
                        && id.ValueKind == JsonValueKind.String && !String.IsNullOrWhiteSpace(id.GetString())
                        ? new(SubmissionResult.Accepted, id.GetString()) : new(SubmissionResult.Retry);
                }
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using JsonDocument result = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
                    return result.RootElement.TryGetProperty("name", out JsonElement name)
                        && name.ValueKind == JsonValueKind.String && name.GetString() == "concurrent_idempotent_requests"
                        ? new(SubmissionResult.Retry) : new(SubmissionResult.Hold);
                }
                return response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500
                    ? new(SubmissionResult.Retry) : new(SubmissionResult.Rejected);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(SubmissionResult.Retry);
            }
            catch (HttpRequestException)
            {
                return new(SubmissionResult.Retry);
            }
            catch (JsonException)
            {
                return new(SubmissionResult.Retry);
            }
        }

        private string Credential(string domain)
            => options.Value.DomainScopedAPIKeys != null && options.Value.DomainScopedAPIKeys.TryGetValue(domain, out string? key)
                ? key : options.Value.GlobalAPIKey ?? String.Empty;
    }
}
