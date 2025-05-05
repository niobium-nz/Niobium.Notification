using System.Text.Encodings.Web;
using System.Text.Unicode;
using Cod;
using Cod.Database.StorageTable;
using Cod.File.Blob;
using Cod.Messaging.ServiceBus;
using Cod.Platform;
using Cod.Platform.Blob;
using Cod.Platform.Notification.Email.Resend;
using Cod.Platform.StorageTable;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Niobium.EmailNotification
{
    internal static class DependencyModule
    {
        public static IServiceCollection AddNotification(this IServiceCollection services, HostBuilderContext context)
        {
            var isDevelopment = context.Configuration.IsDevelopmentEnvironment();

            services.AddApplicationInsightsTelemetryWorkerService();
            services.ConfigureFunctionsApplicationInsights();

            services.AddDatabase(context.Configuration.GetRequiredSection(nameof(StorageTableOptions)))
             .PostConfigure<StorageTableOptions>(opt => opt.EnableInteractiveIdentity = isDevelopment);

            services.AddFile(context.Configuration.GetRequiredSection(nameof(StorageBlobOptions)))
             .PostConfigure<StorageBlobOptions>(opt => opt.EnableInteractiveIdentity = isDevelopment);

            services.AddMessaging(context.Configuration.GetRequiredSection(nameof(ServiceBusOptions)))
             .PostConfigure<ServiceBusOptions>(opt => opt.EnableInteractiveIdentity = isDevelopment);

            services.AddNotification(context.Configuration.GetRequiredSection(nameof(ResendServiceOptions)));
            services.Configure<EmailNotificationOptions>((settings) =>
            {
                context.Configuration.GetSection(nameof(EmailNotificationOptions)).Bind(settings);
            });
            services.AddTransient<IVisitorRiskAssessor, GoogleReCaptchaRiskAssessor>();
            services.AddSingleton(HtmlEncoder.Create(allowedRanges: [UnicodeRanges.BasicLatin, UnicodeRanges.CjkUnifiedIdeographs]));
            services.RegisterDomain<SubscriptionDomain, Subscription>();
            services.RegisterDomainEventHandler<WelcomeSubscriptionTrigger, Subscription>();
            services.AddHttpClient<IVisitorRiskAssessor, GoogleReCaptchaRiskAssessor>((sp, httpClient) =>
            {
                httpClient.BaseAddress = new Uri("https://www.google.com/");
            })
            .AddStandardResilienceHandler();

            return services;
        }
    }
}
