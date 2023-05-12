using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Niobium.EmailNotification
{
    public class EmailNotificationFunction
    {
        private readonly IEmailSender sender;

        public EmailNotificationFunction(IEmailSender sender) => this.sender = sender;

        [Function(nameof(Notification))]
        public async Task Notification([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req, CancellationToken cancellationToken)
        {
            var request = await req.ReadFromJsonAsync<NotificationRequest>(cancellationToken);
            await this.sender.SendEmailAsync(request.Tenant, request.Message, request.Name, request.Contact);
        }
    }
}
