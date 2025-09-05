namespace Niobium.Notification
{
    public partial class TemplateDomain
    {
        public class Deliverable
        {
            public required string From { get; set; }

            public string? FromName { get; set; }

            public required string To { get; set; }

            public string? ToName { get; set; }

            public string? Subject { get; set; }

            public required string Body { get; set; }
        }
    }
}
