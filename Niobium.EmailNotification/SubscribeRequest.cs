using System.ComponentModel.DataAnnotations;

namespace Niobium.EmailNotification
{
    public class SubscribeRequest
    {
        [Required]
        public Guid ID { get; set; }

        [MaxLength(50)]
        public string? Tenant { get; set; }

        [MaxLength(30)]
        public required string Source { get; set; }

        [Required]
        [MaxLength(50)]
        public required string FirstName { get; set; }

        [MaxLength(50)]
        public string? LastName { get; set; }

        [MaxLength(50)]
        [EmailAddress]
        public required string Email { get; set; }

        [Required]
        [MaxLength(5000)]
        public required string Captcha { get; set; }
    }
}
