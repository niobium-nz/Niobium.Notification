using Niobium.Notification.Host;
WebApplication.CreateBuilder(args)
    .AddNotification()
    .Build()
    .UseNotification()
    .Run();
