using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Niobium.Messaging.ServiceBus;
using Niobium.Notification.Delivery;
using Niobium.Notification.Server.Delivery;
using Niobium.Platform.Blob;
using Niobium.Platform.Captcha.ReCaptcha;
using Niobium.Platform.ServiceBus;
using Niobium.Platform.StorageTable;

namespace Niobium.Notification.Server
{
    internal static class DependencyModule
    {
        private static volatile bool loaded;

        public static TBuilder AddNotification<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
        {
            Platform.Functions.DependencyModule.AddPlatform(builder);
            builder.AddDatabase();
            builder.AddFile();
            builder.AddMessaging();
            Platform.Notification.Email.Resend.DependencyModule.AddNotification(builder);
            builder.AddCaptcha();
            builder.AddCore();
            return builder.AddNotification(builder.Configuration.GetSection(nameof(NotificationOptions)).Bind);
        }

        public static TBuilder AddNotification<TBuilder>(this TBuilder builder, Action<NotificationOptions>? options)
             where TBuilder : IHostApplicationBuilder
        {
            if (loaded)
            {
                return builder;
            }

            loaded = true;

            builder.Services.Configure<NotificationOptions>(o => options?.Invoke(o));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<NotificationRetryPolicy>();
            builder.Services.AddHttpClient<IIdempotentNotificationProvider, ResendSubmissionProvider>()
                .ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
                {
                    // Niobium's global HTTP defaults add retries. Durable submission owns them.
                    for (int index = handlers.Count - 1; index >= 0; index--)
                    {
                        if (handlers[index] is ResilienceHandler)
                        {
                            handlers.RemoveAt(index);
                        }
                    }
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

            return builder;
        }
    }
}
