using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cod.Platform;
using Cod.Platform.Notification.Email;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Options;

namespace Niobium.EmailNotification
{
    public class EmailNotificationFunction(
        IOptions<EmailNotificationOptions> options,
        HtmlEncoder encoder,
        IEmailNotificationClient sender,
        IVisitorRiskAssessor assessor)
    {
        private const string TEMPLATE_NAME = "{{NAME}}";
        private const string TEMPLATE_CONTACT = "{{CONTACT}}";
        private const string TEMPLATE_MESSAGE = "{{MESSAGE}}";

        private static readonly JsonSerializerOptions serializationOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        [Function(nameof(Notification))]
        public async Task<IActionResult> Notification(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req,
            CancellationToken cancellationToken)
        {
            var request = await JsonSerializer.DeserializeAsync<NotificationRequest>(req.Body, options: serializationOptions, cancellationToken: cancellationToken);
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(request.Tenant))
            {
                var referer = req.Headers.Referer.SingleOrDefault();
                if (referer != null)
                {
                    request.Tenant = new Uri(referer).Host.ToLower();
                }
            }

            var validationResults = new List<ValidationResult>();
            var validates = Validator.TryValidateObject(request, new ValidationContext(request), validationResults, true);
            if (!validates)
            {
                return new BadRequestObjectResult(validationResults);
            }

            var tenant = request.Tenant!;
            var clientIP = req.GetRemoteIP();
            var lowRisk = await assessor.AssessAsync(request.ID, tenant, request.Token, clientIP, cancellationToken);
            if (!lowRisk)
            {
                return new ForbidResult();
            }

            var recipient = options.Value.Recipients[tenant]
                ?? throw new ApplicationException($"Missing tenant recipient: {tenant}");

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
