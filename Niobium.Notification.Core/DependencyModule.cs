using System.Text.Encodings.Web;
using System.Text.Unicode;
using Cod;
using Cod.Messaging;
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
            services.AddDomainEventHandler<SubscribedEventAdaptor, Subscription>();
            services.AddDomainEventHandler<GreetingsInitiator, Subscription>();
            services.EnableExternalEvent<Subscription, SubscribedEvent>();

            return services;
        }
    }
}
