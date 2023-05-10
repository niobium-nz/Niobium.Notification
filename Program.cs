using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Niobium.EmailNotification;
using SendGrid.Extensions.DependencyInjection;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddSendGrid(options =>
            options.ApiKey = context.Configuration["SENDGRID_APIKEY"]
        );
        services.Configure<EmailOptions>(context.Configuration.GetSection("Email"));
        services.AddTransient<EmailSender>();
    })
    .Build();

host.Run();
