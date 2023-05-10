using System.ComponentModel.DataAnnotations;

namespace Niobium.EmailNotification
{
    public class NotificationRequest
    {
        [Required]
        public Guid? ID { get; set; }

        [Required]
        public string? Tenant { get; set; }

        [Required]
        public string? Message { get; set; }

        public string? Name { get; set; }

        public string? Contact { get; set; }
    }
}
