using System.ComponentModel.DataAnnotations;
using Niobium.Messaging;

namespace Niobium.Notification
{
    public class NotifyCommand : DomainEvent, IUserInput
    {
        [Required]
        public required Guid Tenant { get; set; }

        [Required]
        [MaxLength(50)]
        public required string Channel { get; set; }

        [MaxLength(50)]
        public string? Destination { get; set; }

        [MaxLength(50)]
        public string? DestinationDisplayName { get; set; }

        [Required]
        public required Dictionary<string, string> Parameters { get; set; }

        [MaxLength(5000)]
        public string? Token { get; set; }

        public void Sanitize()
        {
            if (Destination != null)
            {
                Destination = Destination.Trim();
            }

            if (DestinationDisplayName != null)
            {
                DestinationDisplayName = DestinationDisplayName.Trim();
            }
        }
    }
}
