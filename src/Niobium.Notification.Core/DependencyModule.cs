using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Niobium.Messaging;

namespace Niobium.Notification
{
    public static class DependencyModule
    {
        private static volatile bool loaded;

        public static TBuilder AddCore<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
        {
            if (loaded)
            {
                return builder;
            }

            loaded = true;

            builder.Services.AddSingleton(HtmlEncoder.Create(allowedRanges: [UnicodeRanges.BasicLatin, UnicodeRanges.CjkUnifiedIdeographs]));
            builder.Services.RegisterDomainComponents(typeof(DependencyModule));
            builder.Services.EnableExternalEvent<SubscribedEvent, Subscription>();
            return builder;
        }
    }
}
