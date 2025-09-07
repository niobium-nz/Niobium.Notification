using System.ComponentModel.DataAnnotations;

namespace Niobium.Notification
{
    public class NotifyCommand
    {
        [Required]
        public required Guid ID { get; set; }

        [Required]
        [MaxLength(50)]
        public required Guid Tenant { get; set; }

        [Required]
        [MaxLength(50)]
        public required string Channel { get; set; }

        [MaxLength(50)]
        public string? Destination { get; set; }

        [Required]
        public required Dictionary<string, string> Parameters { get; set; }

        [MaxLength(5000)]
        public string? Token { get; set; }
    }
}
