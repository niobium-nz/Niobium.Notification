using System.ComponentModel.DataAnnotations;

namespace Niobium.Notification
{
    public class NotificationRequest
    {
        [Required]
        public Guid ID { get; set; }

        [Required]
        [MaxLength(50)]
        public string? Tenant { get; set; }

        [Required]
        [MaxLength(3000)]
        public required string Message { get; set; }

        [MaxLength(50)]
        public string? Name { get; set; }

        [MaxLength(50)]
        public string? Contact { get; set; }

        [Required]
        [MaxLength(5000)]
        public required string Token { get; set; }
    }
}
