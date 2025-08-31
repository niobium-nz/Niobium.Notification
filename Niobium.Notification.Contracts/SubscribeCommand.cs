using System.ComponentModel.DataAnnotations;
using Niobium;
using Niobium.Messaging;

namespace Niobium.Notification
{
    public class SubscribeCommand : DomainEvent, IUserInput
    {
        [MaxLength(50)]
        public string? Tenant { get; set; }

        [MaxLength(30)]
        public required string Campaign { get; set; }

        [MaxLength(30)]
        public string? Track { get; set; }

        [Required]
        [MaxLength(50)]
        public required string FirstName { get; set; }

        [MaxLength(50)]
        public string? LastName { get; set; }

        [MaxLength(50)]
        [EmailAddress]
        public required string Email { get; set; }

        [MaxLength(5000)]
        public string? Captcha { get; set; }

        public void Sanitize()
        {
            if (Track != null)
            {
                Track = Track.Trim();
            }
            FirstName = FirstName.Trim();

            if (LastName != null)
            {
                LastName = LastName.Trim();
            }

            if (Tenant != null)
            {
                Tenant = Tenant.Trim().ToLowerInvariant();
            }

            Email = Email.Trim().ToLowerInvariant();

            if (Captcha != null)
            {
                Captcha = Captcha.Trim();
            }
        }
    }
}
