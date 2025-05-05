using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Cod.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace Niobium.EmailNotification
{
    public class SubscribeFunction(Func<SubscriptionDomain> domainFactory, IVisitorRiskAssessor assessor)
    {
        private static readonly JsonSerializerOptions serializationOptions = new(JsonSerializerDefaults.Web);

        [Function(nameof(Subscribe))]
        public async Task<IActionResult> Subscribe(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req,
            CancellationToken cancellationToken)
        {
            var request = await JsonSerializer.DeserializeAsync<SubscribeRequest>(req.Body, options: serializationOptions, cancellationToken: cancellationToken);
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

            var domain = domainFactory();
            await domain.SubscribeAsync(tenant, request.Campaign, request.Email, request.FirstName, request.LastName, request.Source, cancellationToken: cancellationToken);
            return new OkResult();
        }
    }
}
