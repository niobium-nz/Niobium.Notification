using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using Niobium.Notification.Server;
using Niobium.Platform;
using Niobium.Platform.Functions;

FunctionsApplicationBuilder builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.AddNotification();
builder.ToMiddlewareHost().UsePlatform();
builder.Build().Run();
