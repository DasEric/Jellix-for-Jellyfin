using System.Reflection;
using System.Runtime.Loader;
using Jellyfin.Plugin.Jellix.Configuration;
using Jellyfin.Plugin.Jellix.Helpers;
using Jellyfin.Plugin.Jellix.Security;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Jellix;

/// <summary>Jellix Jellyfin plugin entry point.</summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "bea64f51-00f3-4535-8fd3-88bcd2785f24";
    private readonly IApplicationPaths _applicationPaths;
    private int _transformationRegistered;
    private int _transformationRegistrationInProgress;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationPaths = applicationPaths;
        Secrets = new SecretStore(DataFolderPath);
        ApplyWebIntegration(Configuration.UserPageEnabled);
    }

    public static Plugin? Instance { get; private set; }

    public SecretStore Secrets { get; }

    public override string Name => "Jellix";

    public override string Description => "Discord companion for Jellyfin accounts, statistics and requests.";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is not PluginConfiguration typed)
        {
            throw new ArgumentException("Unexpected plugin configuration type.", nameof(configuration));
        }

        NormalizeConfiguration(typed);
        base.UpdateConfiguration(typed);
        ApplyWebIntegration(typed.UserPageEnabled);
    }

    public override void OnUninstalling()
    {
        UpdateIndexHtml(false);
        base.OnUninstalling();
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var root = typeof(Plugin).Namespace;
        return
        [
            new PluginPageInfo
            {
                Name = "JellixConfig",
                EmbeddedResourcePath = $"{root}.Web.config.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "chat",
                DisplayName = "Jellix",
            },
            new PluginPageInfo
            {
                Name = "JellixConfigJS",
                EmbeddedResourcePath = $"{root}.Web.config.js",
            },
            new PluginPageInfo
            {
                Name = "JellixLink",
                EmbeddedResourcePath = $"{root}.Web.link.html",
                DisplayName = "Discord verbinden",
            },
            new PluginPageInfo
            {
                Name = "JellixLinkJS",
                EmbeddedResourcePath = $"{root}.Web.link.js",
            },
            new PluginPageInfo
            {
                Name = "JellixPassword",
                EmbeddedResourcePath = $"{root}.Web.password.html",
                DisplayName = "Passwort ändern",
            },
            new PluginPageInfo
            {
                Name = "JellixPasswordJS",
                EmbeddedResourcePath = $"{root}.Web.password.js",
            },
        ];
    }

    private string IndexHtmlPath => Path.Combine(_applicationPaths.WebPath, "index.html");

    private void ApplyWebIntegration(bool enabled)
    {
        UpdateIndexHtml(enabled);
        if (enabled && Volatile.Read(ref _transformationRegistered) == 0
            && Interlocked.CompareExchange(ref _transformationRegistrationInProgress, 1, 0) == 0)
        {
            _ = Task.Run(RegisterTransformationAsync);
        }
    }

    private async Task RegisterTransformationAsync()
    {
        try
        {
            for (var attempt = 0; attempt < 30 && Configuration.UserPageEnabled; attempt++)
            {
                try
                {
                    if (TryRegisterTransformation())
                    {
                        Volatile.Write(ref _transformationRegistered, 1);
                        return;
                    }
                }
                catch (Exception)
                {
                    // The optional plugin may still be starting.
                }

                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _transformationRegistrationInProgress, 0);
        }
    }

    private bool TryRegisterTransformation()
    {
        var assembly = AssemblyLoadContext.All.SelectMany(static value => value.Assemblies)
            .FirstOrDefault(static value => value.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) == true);
        var interfaceType = assembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        var method = interfaceType?.GetMethod("RegisterTransformation");
        if (method is null) return false;
        method.Invoke(null,
        [
            new JObject
            {
                { "id", PluginGuid },
                { "fileNamePattern", "index.html" },
                { "callbackAssembly", GetType().Assembly.FullName },
                { "callbackClass", typeof(TransformationPatches).FullName },
                { "callbackMethod", nameof(TransformationPatches.IndexHtml) },
            },
        ]);
        return true;
    }

    private void UpdateIndexHtml(bool inject)
    {
        try
        {
            if (!File.Exists(IndexHtmlPath)) return;
            var original = File.ReadAllText(IndexHtmlPath);
            var updated = TransformationPatches.ApplyIndexHtml(original, inject);
            if (string.Equals(original, updated, StringComparison.Ordinal)) return;
            var directory = Path.GetDirectoryName(IndexHtmlPath);
            if (directory is null) return;
            var temporary = Path.Combine(directory, Path.GetRandomFileName());
            try
            {
                File.WriteAllText(temporary, updated);
                File.Move(temporary, IndexHtmlPath, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (IOException)
        {
            // Best effort; the registered plugin page remains available.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only jellyfin-web installations need File Transformation.
        }
        catch (Exception)
        {
            // Web integration is optional and must never prevent Jellyfin startup.
        }
    }

    private static void NormalizeConfiguration(PluginConfiguration value)
    {
        value.Language = value.Language == "en" ? "en" : "de";
        value.GuildId = Clean(value.GuildId);
        value.JellyfinPublicUrl = Clean(value.JellyfinPublicUrl).TrimEnd('/');
        value.StreamingRoleId = Clean(value.StreamingRoleId);
        value.RequestRoleId = Clean(value.RequestRoleId);
        value.AdminRoleId = Clean(value.AdminRoleId);
        value.AchievementChannelId = Clean(value.AchievementChannelId);
        value.RequestNotificationChannelId = Clean(value.RequestNotificationChannelId);
        value.NewMediaChannelId = Clean(value.NewMediaChannelId);
        value.AccessRequestChannelId = Clean(value.AccessRequestChannelId);
        value.AdminAlertChannelId = Clean(value.AdminAlertChannelId);
        value.TimeZoneId = Clean(value.TimeZoneId);
        value.AchievementNotificationMode = value.AchievementNotificationMode is "off" or "channel" ? value.AchievementNotificationMode : "dm";
        value.RequestNotificationMode = value.RequestNotificationMode is "off" or "channel" ? value.RequestNotificationMode : "dm";
        value.AdminAlertMode = value.AdminAlertMode == "channel" ? "channel" : "owner-dm";
        value.NowPlayingMode = value.NowPlayingMode is "off" or "public" ? value.NowPlayingMode : "admin";
        value.LinkCodeLifetimeMinutes = Math.Clamp(value.LinkCodeLifetimeMinutes, 1, 30);
        value.PasswordTicketLifetimeMinutes = Math.Clamp(value.PasswordTicketLifetimeMinutes, 1, 30);
        value.CompletedPlaybackPercent = Math.Clamp(value.CompletedPlaybackPercent, 50, 100);
        value.MediaForgePollSeconds = Math.Clamp(value.MediaForgePollSeconds, 15, 3600);
        value.AccessRequestCooldownHours = Math.Clamp(value.AccessRequestCooldownHours, 1, 8760);
        value.StickyDebounceSeconds = Math.Clamp(value.StickyDebounceSeconds, 1, 10);
        value.HealthCheckMinutes = Math.Clamp(value.HealthCheckMinutes, 1, 1440);
        value.AuditRetentionDays = Math.Clamp(value.AuditRetentionDays, 1, 3650);
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
