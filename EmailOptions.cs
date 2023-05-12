using SendGrid.Helpers.Mail;

namespace Niobium.EmailNotification
{
    public class EmailOptions
    {
        public EmailOptions(EmailAddress from, EmailAddress to, string subject, string template)
        {
            this.From = from;
            this.To = to;
            this.Subject = subject;
            this.Template = template;
        }

        public EmailAddress From { get; set; }

        public EmailAddress To { get; set; }

        public string Subject { get; set; }

        public string Template { get; set; }
    }
}
