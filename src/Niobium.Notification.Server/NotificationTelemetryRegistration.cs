using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Niobium.Notification.Delivery;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Niobium.Notification.Server
{
    public static class NotificationTelemetryRegistration
    {
        public static IServiceCollection AddNotificationTelemetry(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddOpenTelemetry().ConfigureResource(resource =>
            {
                // The installed Functions detector runs after Platform's resource configuration.
                // Restore explicit service identity without dropping the detector's other metadata.
                List<KeyValuePair<string, object>> attributes = [];
                string? name = configuration["SERVICE_NAME"];
                string? version = configuration["SERVICE_VERSION"];
                if (!String.IsNullOrWhiteSpace(name))
                    attributes.Add(new("service.name", name));
                if (!String.IsNullOrWhiteSpace(version))
                    attributes.Add(new("service.version", version));
                resource.AddAttributes(attributes);
            }).WithMetrics(metrics => metrics.AddMeter(NotificationTelemetry.MeterName));
            return services;
        }
    }
}
