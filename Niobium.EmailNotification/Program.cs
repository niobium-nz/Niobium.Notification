using System.Text.Encodings.Web;
using System.Text.Unicode;
using Google.Cloud.RecaptchaEnterprise.V1;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Niobium.EmailNotification;
using SendGrid.Extensions.DependencyInjection;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(s =>
    {
        s.AddApplicationInsightsTelemetryWorkerService();
        s.ConfigureFunctionsApplicationInsights();

        s.AddSendGrid(options =>
            options.ApiKey = "FROMKEYVAULT"
        );
        s.AddTransient<IVisitorRiskAssessor, GoogleReCaptchaRiskAssessor>();
        s.AddTransient<IEmailSender, SendGridEmailSender>();
        s.AddTransient<TenantConfigurationProvider>();
        s.AddSingleton(HtmlEncoder.Create(
            allowedRanges: [UnicodeRanges.BasicLatin, UnicodeRanges.CjkUnifiedIdeographs]));
        s.AddSingleton(new RecaptchaEnterpriseServiceClientBuilder { JsonCredentials = "FROMKEYVAULT" }.Build());
    })
    .Build();

host.Run();
