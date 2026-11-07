namespace Niobium.Notification
{
    public class NotificationOptions
    {
        // Enable only after reviewing legacy-operation cutover and live verification.
        public bool EnableDurableSubmission { get; set; }

        public required string SelfHostName { get; set; }

        public required string TemplateFolder { get; set; }
    }
}
