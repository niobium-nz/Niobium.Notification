using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace Niobium.EmailNotification
{
    public class EmailNotificationFunction(IEmailSender sender, IVisitorRiskAssessor assessor)
    {
        private static readonly JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true
        };

        [Function(nameof(Notification))]
        public async Task<IActionResult> Notification(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req,
            CancellationToken cancellationToken)
        {
            var request = await JsonSerializer.DeserializeAsync<NotificationRequest>(req.Body, options: options, cancellationToken: cancellationToken);

            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.ID);
            ArgumentNullException.ThrowIfNull(request.Tenant);
            ArgumentNullException.ThrowIfNull(request.Message);
            ArgumentNullException.ThrowIfNull(request.Token);

            var validationResults = new List<ValidationResult>();
            var validates = Validator.TryValidateObject(request, new ValidationContext(request), validationResults, true);
            if (!validates)
            {
                return new BadRequestObjectResult(validationResults);
            }

            var lowRisk = await assessor.AssessAsync(request.Token, "contact-us", cancellationToken);
            if (!lowRisk)
            {
                return new ForbidResult();
            }

            var success = await sender.SendEmailAsync(request.Tenant, request.Message, request.Name, request.Contact, cancellationToken);
            var statuscode = success ? HttpStatusCode.Created : HttpStatusCode.InternalServerError;
            return new StatusCodeResult((int)statuscode);
        }
    }
}
