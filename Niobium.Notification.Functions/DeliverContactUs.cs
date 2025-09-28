using System.Net;
using System.Text.Encodings.Web;
using Azure.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Niobium.Platform;
using Niobium.Platform.Captcha.ReCaptcha;
using FromBodyAttribute = Microsoft.Azure.Functions.Worker.Http.FromBodyAttribute;

namespace Niobium.Notification.Functions
{
    public class DeliverContactUs(
        HtmlEncoder encoder,
        NotificationFlow flow,
        IVisitorRiskAssessor assessor,
        ILogger<DeliverContactUs> logger)
    {
        [Function(nameof(ContactUs))]
        public async Task<IActionResult> ContactUs(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req,
            [FromBody] ContactUsRequest request,
            CancellationToken cancellationToken)
        {
            _ = request.TryValidate(out var validationState);
            if (!validationState.IsValid)
            {
                return validationState.MakeResponse();
            }

            string? origin = req.Headers.Origin.SingleOrDefault();
            var referer = req.Headers.Referer.SingleOrDefault();
            var hostname = req.GetSourceHostname();
            logger.LogError("ContactUs request from {origin} with referer {referer} and hostname {hostname}", origin, referer, hostname);

            _ = await assessor.AssessAsync(request.Token, requestID: request.ID.ToString(), cancellationToken: cancellationToken);
            await flow.RunAsync(new NotifyCommand
            {
                ID = request.ID.ToString(),
                Channel = Constants.ContactUsChannel,
                Tenant = request.Tenant,
                Parameters = new Dictionary<string, string>
                 {
                     { nameof(request.Name), !String.IsNullOrWhiteSpace(request.Name) ? encoder.Encode(request.Name) : "unspecified" },
                     { nameof(request.Contact),!String.IsNullOrWhiteSpace(request.Contact) ? encoder.Encode(request.Contact) : "unspecified"  },
                     { nameof(request.Message), encoder.Encode(request.Message) }
                 },
            }, cancellationToken);

            return new StatusCodeResult((int)HttpStatusCode.Created);
        }
    }
}
