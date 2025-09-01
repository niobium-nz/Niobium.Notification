using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Niobium.Messaging;

namespace Niobium.Notification
{
    public static class DependencyModule
    {
        private static volatile bool loaded;

        public static void AddCore(this IFunctionsWorkerApplicationBuilder builder)
        {
            if (loaded)
            {
                return;
            }

            loaded = true;

            _ = builder.Services.AddSingleton(HtmlEncoder.Create(allowedRanges: [UnicodeRanges.BasicLatin, UnicodeRanges.CjkUnifiedIdeographs]));
            _ = builder.Services.RegisterDomainComponents(typeof(DependencyModule));
            _ = builder.Services.EnableExternalEvent<SubscribedEvent, Subscription>();
        }
    }
}
