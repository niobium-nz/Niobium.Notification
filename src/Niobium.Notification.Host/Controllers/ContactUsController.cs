using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;
using Niobium.Platform;
using Niobium.Platform.Captcha.ReCaptcha;

namespace Niobium.Notification.Host.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ContactUsController(
        HtmlEncoder encoder,
        NotificationFlow flow,
        IVisitorRiskAssessor assessor)
        : ControllerBase
    {
        [HttpPost]
        public async Task<IActionResult> Action([FromBody] ContactUsRequest request, CancellationToken cancellationToken)
        {
            request.TryValidate(out ValidationState? validationState);
            if (!validationState.IsValid)
            {
                return validationState.MakeResponse();
            }

            await assessor.AssessAsync(request.Token, requestID: request.ID.ToString(), cancellationToken: cancellationToken);
            await flow.RunAsync(new NotifyCommand
            {
                ID = request.ID.ToString(),
                Channel = Constants.ContactUsChannel,
                Tenant = request.Tenant,
                Parameters = new Dictionary<string, object>
                 {
                     { nameof(request.Name), !String.IsNullOrWhiteSpace(request.Name) ? encoder.Encode(request.Name) : "unspecified" },
                     { nameof(request.Contact),!String.IsNullOrWhiteSpace(request.Contact) ? encoder.Encode(request.Contact) : "unspecified"  },
                     { nameof(request.Message), encoder.Encode(request.Message) }
                 },
            }, cancellationToken);

            return new OkObjectResult(request);
        }
    }
}
