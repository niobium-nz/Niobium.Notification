using System.ComponentModel.DataAnnotations;
using Niobium.Messaging;

namespace Niobium.Notification
{
    public class SubscribeCommand : DomainEvent, IUserInput
    {
        [Required]
        public required Guid Tenant { get; set; }

        [MaxLength(30)]
        public required string Campaign { get; set; }

        [MaxLength(30)]
        public string? Track { get; set; }

        [MaxLength(50)]
        public string? FirstName { get; set; }

        [MaxLength(50)]
        public string? LastName { get; set; }

        [MaxLength(50)]
        [EmailAddress]
        public required string Email { get; set; }

        [MaxLength(5000)]
        public string? Captcha { get; set; }

        public void Sanitize()
        {
            if (this.Track != null)
            {
                this.Track = this.Track.Trim();
            }

            if (this.FirstName != null)
            {
                this.FirstName = this.FirstName.Trim();
            }

            if (this.LastName != null)
            {
                this.LastName = this.LastName.Trim();
            }

            this.Email = this.Email.Trim().ToLowerInvariant();

            if (this.Captcha != null)
            {
                this.Captcha = this.Captcha.Trim();
            }
        }
    }
}
