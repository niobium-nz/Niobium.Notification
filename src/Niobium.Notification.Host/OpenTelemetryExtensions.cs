using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Niobium.Notification.Host
{
    internal static class OpenTelemetryExtensions
    {
        public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
        {
            string? applicationInsightsConnectionString = builder.Configuration.GetValue<string>("APPLICATIONINSIGHTS_CONNECTION_STRING");
            string? otlpEndpoint = builder.Configuration.GetValue<string>("OTEL_EXPORTER_OTLP_ENDPOINT");
            string? environment = builder.Environment.EnvironmentName;
            Dictionary<string, object> resourceAttributes = new()
            {
                { "service.instance.id", Environment.MachineName },
                { "service.name", builder.Configuration.GetValue<string>("SERVICE_NAME") ?? "unknown-service" },
                { "service.version", builder.Configuration.GetValue<string>("SERVICE_VERSION") ?? "1.0.0-prerelease" },
                { "deployment.environment", environment ?? "local" }
            };

            OpenTelemetryBuilder telemetryBuilder = builder.Services.AddOpenTelemetry();
            telemetryBuilder.ConfigureResource(resourceBuilder => resourceBuilder.AddAttributes(resourceAttributes));

            if (!String.IsNullOrWhiteSpace(applicationInsightsConnectionString))
            {
                telemetryBuilder.UseAzureMonitor(options => options.ConnectionString = applicationInsightsConnectionString);
            }

            telemetryBuilder.WithTracing(builder =>
            {
                if (!String.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    builder.AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(otlpEndpoint);
                        o.Protocol = OtlpExportProtocol.Grpc;
                    });
                }
            });
            telemetryBuilder.WithMetrics(builder =>
            {
                if (!String.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    builder.AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(otlpEndpoint);
                        o.Protocol = OtlpExportProtocol.Grpc;
                    });
                }
            });

            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            ILoggingBuilder logBuilder = builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;

                if (!String.IsNullOrWhiteSpace(applicationInsightsConnectionString))
                {
                    logging.AddAzureMonitorLogExporter(options => options.ConnectionString = applicationInsightsConnectionString);
                }

                if (!String.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    logging.AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(otlpEndpoint);
                        o.Protocol = OtlpExportProtocol.Grpc;
                    });
                }
            });

#if DEBUG
            logBuilder.SetMinimumLevel(LogLevel.Debug);
#endif

            return builder;
        }
    }
}
