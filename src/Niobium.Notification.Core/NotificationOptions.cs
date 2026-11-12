namespace Niobium.Notification
{
    public class NotificationOptions
    {
        // Enable only after reviewing legacy-operation cutover and live verification.
        public bool EnableDurableSubmission { get; set; }

        // An absent filter preserves the existing all-tenant enable switch.
        // Keep selected tenants enrolled while their operations can be replayed.
        // Bind one raw comma-separated value: GUID arrays can silently lose invalid entries.
        public string? DurableSubmissionTenants { get; set; }

        public bool UsesDurableSubmission(Guid tenant)
        {
            if (!EnableDurableSubmission)
            {
                return false;
            }
            if (DurableSubmissionTenants == null)
            {
                return true;
            }
            bool selected = false;
            foreach (string value in DurableSubmissionTenants.Split(','))
            {
                bool parsed = Guid.TryParse(value, out Guid configuredTenant);
                if (!parsed || configuredTenant == Guid.Empty)
                {
                    throw new InvalidOperationException("The durable submission tenant filter contains an invalid tenant ID.");
                }
                selected |= configuredTenant == tenant;
            }
            return selected;
        }

        public required string SelfHostName { get; set; }

        public required string TemplateFolder { get; set; }
    }
}
