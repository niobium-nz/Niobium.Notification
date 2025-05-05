namespace Niobium.EmailNotification
{
    internal class GoogleAdsLeadForm
    {
        public required string LeadID { get; set; }

        public required GoogleAdsLeadFormColumn[] UserColumnData { get; set; }

        public long CampaignID { get; set; }

        public required string GoogleKey { get; set; }

        public bool IsTest { get; set; }
    }
}
