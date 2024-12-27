namespace Niobium.EmailNotification
{
    public class EmailOptions
    {
        public string? From { get; set; }

        public required string To { get; set; }

        public string? Subject { get; set; }

        public string? Template { get; set; }
    }
}
