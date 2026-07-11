using System.Net;
using Microsoft.AspNetCore.Mvc;
using Niobium.Platform;
using Niobium.Platform.Captcha.ReCaptcha;

namespace Niobium.Notification.Host.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class SubscribeController(Func<SubscriptionDomain> domainFactory, IVisitorRiskAssessor assessor) : ControllerBase
    {
        [HttpPost]
        public async Task<IActionResult> Action([FromBody] SubscribeCommand command,
            CancellationToken cancellationToken)
        {
            if (command.Token == null)
            {
                return new StatusCodeResult((int)HttpStatusCode.Forbidden);
            }

            command.TryValidate(out ValidationState? validationState);
            if (!validationState.IsValid)
            {
                return validationState.MakeResponse();
            }

            await assessor.AssessAsync(command.Token, requestID: command.ID, cancellationToken: cancellationToken);

            await domainFactory().SubscribeAsync(command.Tenant, command.Campaign, command.Email, command.FirstName, command.LastName, command.Track, this.Request.GetRemoteIP(), cancellationToken: cancellationToken);
            return new OkResult();
        }
    }
}
