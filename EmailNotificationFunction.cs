using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Encodings.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Niobium.EmailNotification
{
    public class EmailNotificationFunction
    {
        private readonly IEmailSender sender;
        private readonly HtmlEncoder encoder;
        private readonly IVisitorRiskAssessor assessor;

        public EmailNotificationFunction(IEmailSender sender, HtmlEncoder encoder, IVisitorRiskAssessor assessor)
        {
            this.sender = sender;
            this.encoder = encoder;
            this.assessor = assessor;
        }

        [Function(nameof(Notification))]
        public async Task<HttpResponseData> Notification([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req, CancellationToken cancellationToken)
        {
            var request = await req.ReadFromJsonAsync<NotificationRequest>(cancellationToken);
            if (request == null)
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            var badRequest = await req.ValidateAsync(request, cancellationToken);
            if (badRequest != null)
            {
                return badRequest;
            }

            ArgumentNullException.ThrowIfNull(request.ID);
            ArgumentNullException.ThrowIfNull(request.Tenant);
            ArgumentNullException.ThrowIfNull(request.Message);
            ArgumentNullException.ThrowIfNull(request.Token);

            var lowRisk = await assessor.AssessAsync(request.Token, "contact-us", cancellationToken);
            if (!lowRisk)
            {
                var response = req.CreateResponse();
                var validationResults = new ValidationResult[]
                {
                    new ValidationResult("You are unable to prove that you are a human.", new string[]{ nameof(request.Token) }),
                };
                await response.WriteAsJsonAsync(validationResults, cancellationToken);
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            request.Message = this.encoder.Encode(request.Message);

            if (request.Name != null)
            {
                request.Name = this.encoder.Encode(request.Name);
            }

            if (request.Contact != null)
            {
                request.Contact = this.encoder.Encode(request.Contact);
            }

            var success = await this.sender.SendEmailAsync(request.Tenant, request.Message, request.Name, request.Contact, cancellationToken);
            return !success ? req.CreateResponse(HttpStatusCode.InternalServerError) : req.CreateResponse(HttpStatusCode.Created);
        }
    }
}
