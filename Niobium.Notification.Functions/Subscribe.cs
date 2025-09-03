using Azure.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Niobium;
using Niobium.Platform;
using Niobium.Platform.Captcha.ReCaptcha;
using FromBodyAttribute = Microsoft.Azure.Functions.Worker.Http.FromBodyAttribute;

namespace Niobium.Notification.Functions
{
    public class Subscribe(
        Func<SubscriptionDomain> domainFactory,
        IVisitorRiskAssessor assessor)
    {
        [Function(nameof(Subscribe))]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req,
            [FromBody] SubscribeCommand command,
            CancellationToken cancellationToken)
        {
            var origin = req.GetSourceHostname();
            if (String.IsNullOrWhiteSpace(origin))
            {
                origin = command.Tenant;
            }

            if (String.IsNullOrWhiteSpace(origin))
            {
                return new BadRequestObjectResult(new { Error = "Tenant is required." });
            }

            if (command.Captcha == null)
            {
                return new ForbidResult();
            }

            command.TryValidate(out var validationState);
            if (!validationState.IsValid)
            {
                return validationState.MakeResponse();
            }

            await assessor.AssessAsync(command.Captcha, requestID: command.ID, hostname: origin, cancellationToken: cancellationToken);

            await domainFactory().SubscribeAsync(origin, command.Campaign, command.Email, command.FirstName, command.LastName, command.Track, req.GetRemoteIP(), cancellationToken: cancellationToken);
            return new OkResult();
        }
    }
}
