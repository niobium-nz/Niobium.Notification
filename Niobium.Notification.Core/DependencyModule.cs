using System.Text.Encodings.Web;
using System.Text.Unicode;
using Cod;
using Microsoft.Extensions.DependencyInjection;

namespace Niobium.Notification
{
    public static class DependencyModule
    {
        private static volatile bool loaded;

        public static IServiceCollection AddCore(this IServiceCollection services)
        {
            if (loaded)
            {
                return services;
            }

            loaded = true;

            services.AddSingleton(HtmlEncoder.Create(allowedRanges: [UnicodeRanges.BasicLatin, UnicodeRanges.CjkUnifiedIdeographs]));
            services.AddDomain<SubscriptionDomain, Subscription>();
            services.AddDomainEventHandler<WelcomeSubscriptionTrigger, Subscription>();

            return services;
        }
    }
}
