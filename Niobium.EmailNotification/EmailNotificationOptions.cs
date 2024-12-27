namespace Niobium.EmailNotification
{
    public class EmailNotificationOptions
    {
        public required string From { get; set; }

        public required string Subject { get; set; }

        public required string Template { get; set; }

        public required Dictionary<string, string> Secrets { get; set; }

        public required Dictionary<string, string> Recipients { get; set; }
    }
}
