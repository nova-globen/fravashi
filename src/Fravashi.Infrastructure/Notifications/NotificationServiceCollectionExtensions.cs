using Fravashi.Application.Autopilot;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Application.WorkItems;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Notifications;

public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INotificationHistory, InMemoryNotificationHistory>();
        var hasToken = !string.IsNullOrWhiteSpace(configuration["Telegram:BotToken"]);
        var hasChat = !string.IsNullOrWhiteSpace(configuration["Telegram:ChatId"]);
        if (hasToken && hasChat)
        {
            services.AddSingleton<TelegramNotificationService>();
            services.AddSingleton<INotificationService>(sp => new NotificationHubService(
                sp.GetRequiredService<INotificationHistory>(),
                sp.GetRequiredService<TelegramNotificationService>()));
        }
        else
        {
            services.AddSingleton<INotificationService>(sp => new NotificationHubService(
                sp.GetRequiredService<INotificationHistory>()));
        }

        // Backlog 304 (ADR 0031 decision 12): everything waiting on a person, and the one channel this
        // version ships to tell a person about it (a Windows toast through fravashi.exe's tray). Registered
        // unconditionally; both are safe no-ops with nothing waiting or no bridge configured.
        // Work item #356: dismissing a parked notice, kept in the key-value store. Optional so a wiring without
        // the store (some tests) simply never hides one.
        services.AddSingleton<IParkedNoticeDismissals>(sp =>
            new ConfigurationParkedNoticeDismissals(sp.GetRequiredService<IConfigurationStore>()));
        services.AddSingleton<IWaitingForPersonList>(sp => new WaitingForPersonList(
            sp.GetRequiredService<LiveSessionRegistry>(),
            sp.GetRequiredService<IAutopilotRunStore>(),
            sp.GetRequiredService<ITrackerResolver>(),
            sp.GetService<IConfigurationStore>() is { } store ? new ConfigurationParkedNoticeDismissals(store) : null));
        services.AddSingleton<IWaitingChannel>(sp => new WindowsToastWaitingChannel(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetService<ILogger<WindowsToastWaitingChannel>>()));
        services.AddHostedService(sp => new WaitingNotificationHost(
            sp.GetRequiredService<IWaitingForPersonList>(),
            sp.GetServices<IWaitingChannel>(),
            logger: sp.GetService<ILogger<WaitingNotificationHost>>()));

        return services;
    }
}
