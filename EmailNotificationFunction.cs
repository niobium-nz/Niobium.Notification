using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Niobium.EmailNotification
{
    public class EmailNotificationFunction
    {
        private readonly IEmailSender sender;

        public EmailNotificationFunction(IEmailSender sender) => this.sender = sender;

        [Function(nameof(Notification))]
        public async Task<HttpResponseData> Notification([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req, CancellationToken cancellationToken)
        {
            var request = await req.ReadFromJsonAsync<NotificationRequest>(cancellationToken);
            if (request == null)
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            var validationResults = new List<ValidationResult>();
            var validates = Validator.TryValidateObject(request, new ValidationContext(request), validationResults, true);
            if (!validates)
            {
                var response = req.CreateResponse();
                await response.WriteAsJsonAsync(validationResults, cancellationToken);
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            ArgumentNullException.ThrowIfNull(request.ID);
            ArgumentNullException.ThrowIfNull(request.Tenant);
            ArgumentNullException.ThrowIfNull(request.Message);

            var success = await this.sender.SendEmailAsync(request.Tenant, request.Message, request.Name, request.Contact);
            if (!success)
            {
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            return req.CreateResponse(HttpStatusCode.Created);
        }
    }
}
