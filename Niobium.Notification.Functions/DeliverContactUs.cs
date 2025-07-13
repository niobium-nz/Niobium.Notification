using System.Net;
using System.Text.Encodings.Web;
using Cod;
using Cod.Platform;
using Cod.Platform.Captcha.ReCaptcha;
using Cod.Platform.Notification.Email;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Options;
using ApplicationException = Cod.ApplicationException;
using FromBodyAttribute = Microsoft.Azure.Functions.Worker.Http.FromBodyAttribute;
using InternalError = Cod.Platform.InternalError;

namespace Niobium.Notification.Functions
{
    public class DeliverContactUs(
        IOptions<NotificationOptions> options,
        HtmlEncoder encoder,
        IEmailNotificationClient sender,
        IVisitorRiskAssessor assessor)
    {
        private const string TEMPLATE_NAME = "{{NAME}}";
        private const string TEMPLATE_CONTACT = "{{CONTACT}}";
        private const string TEMPLATE_MESSAGE = "{{MESSAGE}}";

        [Function(nameof(Notification))]
        public async Task<IActionResult> Notification(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req,
            [FromBody] NotificationRequest request,
            CancellationToken cancellationToken)
        {
            var tenant = req.GetTenant();
            if (string.IsNullOrWhiteSpace(tenant))
            {
                return new BadRequestObjectResult(new { Error = "Tenant is required." });
            }
            request.Tenant = tenant;

            request.TryValidate(out var validationState);
            if (!validationState.IsValid)
            {
                return validationState.MakeResponse();
            }

            await assessor.AssessAsync(request.Token, requestID: request.ID.ToString(), tenant: request.Tenant, cancellationToken: cancellationToken);

            var recipient = options.Value.Recipients[request.Tenant]
                ?? throw new ApplicationException(InternalError.InternalServerError, $"Missing tenant recipient: {request.Tenant}");

            var message = encoder.Encode(request.Message);
            var name = request.Name ?? "unspecified";
            name = encoder.Encode(name);
            var contact = request.Contact ?? "unspecified";
            contact = encoder.Encode(contact);
            var template = options.Value.Template;

            var notification = template.Replace(TEMPLATE_NAME, name)
                .Replace(TEMPLATE_CONTACT, contact)
                .Replace(TEMPLATE_MESSAGE, message);

            var success = await sender.SendAsync(
                new EmailAddress { Address = options.Value.From },
                [recipient],
                options.Value.Subject,
                notification,
                cancellationToken);
            var statuscode = success ? HttpStatusCode.Created : HttpStatusCode.InternalServerError;
            return new StatusCodeResult((int)statuscode);
        }
    }
}
