using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
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
            if (command.Token == null)
            {
                return new StatusCodeResult((int)HttpStatusCode.Forbidden);
            }

            _ = command.TryValidate(out var validationState);
            if (!validationState.IsValid)
            {
                return validationState.MakeResponse();
            }

            _ = await assessor.AssessAsync(command.Token, requestID: command.ID, cancellationToken: cancellationToken);

            await domainFactory().SubscribeAsync(command.Tenant, command.Campaign, command.Email, command.FirstName, command.LastName, command.Track, req.GetRemoteIP(), cancellationToken: cancellationToken);
            return new OkResult();
        }
    }
}
