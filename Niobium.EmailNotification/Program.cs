using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Cod.Platform.Notification.Email.Resend;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Niobium.EmailNotification;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
#if DEBUG
    .ConfigureAppConfiguration(builder =>
        builder.SetBasePath(new FileInfo(Assembly.GetExecutingAssembly().Location).DirectoryName ?? throw new Exception())
        .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true))
#endif
    .ConfigureServices((ctx, s) =>
    {
        s.AddApplicationInsightsTelemetryWorkerService();
        s.ConfigureFunctionsApplicationInsights();

        s.AddNotification(ctx.Configuration.GetRequiredSection(nameof(ResendServiceOptions)));
        s.Configure<EmailNotificationOptions>((settings) =>
        {
            ctx.Configuration.GetSection(nameof(EmailNotificationOptions)).Bind(settings);
        });
        s.AddTransient<IVisitorRiskAssessor, GoogleReCaptchaRiskAssessor>();
        s.AddSingleton(HtmlEncoder.Create(allowedRanges: [UnicodeRanges.BasicLatin, UnicodeRanges.CjkUnifiedIdeographs]));
        s.AddHttpClient<IVisitorRiskAssessor, GoogleReCaptchaRiskAssessor>((sp, httpClient) =>
        {
            httpClient.BaseAddress = new Uri("https://www.google.com/");
        })
        .AddStandardResilienceHandler();
    }).Build();

host.Run();
