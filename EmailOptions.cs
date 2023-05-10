using SendGrid.Helpers.Mail;

namespace Niobium.EmailNotification
{
    public class EmailOptions
    {
        public EmailAddress? From { get; set; }

        public Dictionary<string, EmailAddress>? To { get; set; }

        public Dictionary<string, string>? Subject { get; set; }

        public Dictionary<string, string>? Template { get; set; }
    }
}
