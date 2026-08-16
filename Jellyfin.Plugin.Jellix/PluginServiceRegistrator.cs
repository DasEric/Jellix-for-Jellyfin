using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Discord;
using Jellyfin.Plugin.Jellix.Integrations;
using Jellyfin.Plugin.Jellix.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellix;

/// <summary>Registers Jellix services in Jellyfin's dependency injection container.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<JellixDatabase>();
        serviceCollection.AddSingleton<AccountLinkService>();
        serviceCollection.AddSingleton<PasswordTicketService>();
        serviceCollection.AddSingleton<OperationRateLimiter>();
        serviceCollection.AddSingleton<AchievementService>();
        serviceCollection.AddSingleton<MediaForgeBridgeClient>();
        serviceCollection.AddHttpClient("JellixUpdates", static client => client.Timeout = TimeSpan.FromSeconds(15));

        serviceCollection.AddSingleton<DiscordBotService>();
        serviceCollection.AddHostedService(static services => services.GetRequiredService<DiscordBotService>());
        serviceCollection.AddHostedService<PlaybackTrackingService>();
        serviceCollection.AddHostedService<LibraryNotificationService>();
        serviceCollection.AddHostedService<MediaForgeMonitoringService>();
        serviceCollection.AddHostedService<HealthMonitoringService>();
    }
}
