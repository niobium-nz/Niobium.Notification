using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Cod;
using Cod.Messaging;
using Cod.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace Niobium.EmailNotification
{
    public class SubscribeFunction(
        IRepository<Subscription> repo,
        IMessagingBroker<Subscription> queue,
        IVisitorRiskAssessor assessor)
    {
        private static readonly JsonSerializerOptions serializationOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        [Function(nameof(Subscribe))]
        public async Task<IActionResult> Subscribe(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req,
            CancellationToken cancellationToken)
        {
            var request = await System.Text.Json.JsonSerializer.DeserializeAsync<SubscribeRequest>(req.Body, options: serializationOptions, cancellationToken: cancellationToken);
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(request.Tenant))
            {
                var referer = req.Headers.Referer.SingleOrDefault();
                if (referer != null)
                {
                    request.Tenant = new Uri(referer).Host.ToLower();
                }
            }

            request.Format();
            var validationResults = new List<ValidationResult>();
            var validates = Validator.TryValidateObject(request, new ValidationContext(request), validationResults, true);
            if (!validates)
            {
                return new BadRequestObjectResult(validationResults);
            }

            var tenant = request.Tenant!;
            var clientIP = req.GetRemoteIP();
            var lowRisk = await assessor.AssessAsync(request.ID, tenant, request.Captcha, clientIP, cancellationToken);
            if (!lowRisk)
            {
                return new ForbidResult();
            }

            var newSubscription = new Subscription
            {
                Belonging = Subscription.BuildBelonging(tenant, request.Campaign),
                Email = request.Email,
                FirstName = request.FirstName,
                LastName = request.LastName,
                Source = request.Source,
                Subscribed = DateTimeOffset.UtcNow,
                Unsubscribed = null,
            };

            await repo.CreateAsync(newSubscription, replaceIfExist: true, cancellationToken: cancellationToken);
            await queue.EnqueueAsync(new MessagingEntry<Subscription>
            {
                ID = newSubscription.GetFullID(),
                Value = newSubscription,
            });

            return new OkResult();
        }
    }
}
