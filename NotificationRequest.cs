using System.ComponentModel.DataAnnotations;

namespace Niobium.EmailNotification
{
    public class NotificationRequest
    {
        [Required]
        public Guid? ID { get; set; }

        [Required]
        [MaxLength(50)]
        public string? Tenant { get; set; }

        [Required]
        [MaxLength(3000)]
        public string? Message { get; set; }

        [MaxLength(50)]
        public string? Name { get; set; }

        [MaxLength(50)]
        public string? Contact { get; set; }

        [Required]
        [MaxLength(500)]
        public string? Token { get; set; }
    }
}
