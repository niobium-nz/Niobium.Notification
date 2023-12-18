using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Google.Cloud.RecaptchaEnterprise.V1;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Niobium.EmailNotification;
using SendGrid.Extensions.DependencyInjection;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
#if DEBUG
    .ConfigureAppConfiguration(builder =>
        builder.SetBasePath(new FileInfo(Assembly.GetExecutingAssembly().Location).DirectoryName)
        .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true))
#endif
    .ConfigureServices(s =>
    {
        s.AddApplicationInsightsTelemetryWorkerService();
        s.ConfigureFunctionsApplicationInsights();

        s.AddSendGrid((sp, options) => options.ApiKey = sp.GetService<IConfiguration>()?["SENDGRID_API_KEY"]);
        s.AddTransient<IVisitorRiskAssessor, GoogleReCaptchaRiskAssessor>();
        s.AddTransient<IEmailSender, SendGridEmailSender>();
        s.AddTransient<TenantConfigurationProvider>();
        s.AddSingleton(HtmlEncoder.Create(allowedRanges: [UnicodeRanges.BasicLatin, UnicodeRanges.CjkUnifiedIdeographs]));
        s.AddSingleton(sp => new RecaptchaEnterpriseServiceClientBuilder
        { 
            JsonCredentials = sp.GetService<IConfiguration>()?["GOOGLE_RECAPTCHA_JSON_CREDENTIALS"]
        }.Build());
    }).Build();

host.Run();
