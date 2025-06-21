using System.Text.Json;
using Cod;
using Cod.Platform;
using Cod.Platform.Captcha.ReCaptcha;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Niobium.Notification.Functions
{
    public class Subscribe(
        Func<SubscriptionDomain> domainFactory,
        IVisitorRiskAssessor assessor,
        ILogger<Subscribe> logger)
    {
        private static readonly JsonSerializerOptions serializationOptions = new(JsonSerializerDefaults.Web);

        [Function(nameof(Subscribe))]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req,
            CancellationToken cancellationToken)
        {
            var request = await JsonSerializer.DeserializeAsync<SubscribeCommand>(req.Body, options: serializationOptions, cancellationToken: cancellationToken);
            if (request == null)
            {
                return new BadRequestResult();
            }

            if (string.IsNullOrWhiteSpace(request.Tenant))
            {
                var referer = req.Headers.Referer.SingleOrDefault();
                if (referer != null)
                {
                    request.Tenant = new Uri(referer).Host.ToLower();
                }
            }

            request.TryValidate(out var validationState);
            if (!validationState.IsValid)
            {
                logger.LogWarning("Validation failed for order request: {Errors}", JsonSerializer.Serialize(validationState.ToDictionary(), serializationOptions));
                return validationState.MakeResponse();
            }

            if (request.Captcha == null)
            {
                return new ForbidResult();
            }

            var risk = await req.AssessRiskAsync(assessor, request.ID, request.Captcha, logger, cancellationToken);
            if (risk != null)
            {
                return risk;
            }

            await domainFactory().SubscribeAsync(request.Tenant!, request.Campaign, request.Email, request.FirstName, request.LastName, request.Source, req.GetRemoteIP(), cancellationToken: cancellationToken);
            return new OkResult();
        }
    }
}
