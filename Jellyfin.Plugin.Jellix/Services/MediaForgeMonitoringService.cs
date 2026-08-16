using System.Text.Json;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Discord;
using Jellyfin.Plugin.Jellix.Integrations;
using Jellyfin.Plugin.Jellix.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellix.Services;

/// <summary>Observes MediaForge without becoming a second request data source.</summary>
public sealed class MediaForgeMonitoringService : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogCheckFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(3101, "MediaForgeMonitorFailed"),
        "Jellix MediaForge monitoring failed");
    private readonly MediaForgeBridgeClient _bridge;
    private readonly JellixDatabase _database;
    private readonly IUserManager _userManager;
    private readonly DiscordBotService _discord;
    private readonly ILogger<MediaForgeMonitoringService> _logger;

    public MediaForgeMonitoringService(
        MediaForgeBridgeClient bridge,
        JellixDatabase database,
        IUserManager userManager,
        DiscordBotService discord,
        ILogger<MediaForgeMonitoringService> logger)
    {
        _bridge = bridge;
        _database = database;
        _userManager = userManager;
        _discord = discord;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _database.InitializeAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCheckFailure(_logger, exception);
            return;
        }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.MediaForgeEnabled == true)
            {
                try
                {
                    await PollAsync(stoppingToken).ConfigureAwait(false);
                    await UpdateIncidentAsync("mediaforge-unreachable", false, "MediaForge wieder erreichbar.", "MediaForge is reachable again.", stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is MediaForgeBridgeException or MediaForgeBridgeUnavailableException)
                {
                    LogCheckFailure(_logger, exception);
                    await UpdateIncidentAsync("mediaforge-unreachable", true, "MediaForge ist nicht erreichbar oder inkompatibel.", "MediaForge is unavailable or incompatible.", stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogCheckFailure(_logger, exception);
                }
            }

            var seconds = Math.Clamp(config?.MediaForgePollSeconds ?? 60, 15, 3600);
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        if (!_bridge.IsAvailable)
        {
            throw new MediaForgeBridgeUnavailableException("MediaForge Jellix bridge missing.");
        }

        using (var status = await _bridge.InvokeAsync("status", Guid.Empty, "Jellix", null, cancellationToken).ConfigureAwait(false))
        {
            var root = status.RootElement;
            if (!root.TryGetProperty("healthy", out var healthyValue) || healthyValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("apiKeyValid", out var keyValue) || keyValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new MediaForgeBridgeException("MediaForge returned an invalid status response.");
            }

            var healthy = healthyValue.GetBoolean();
            var apiKeyValid = keyValue.GetBoolean();
            await UpdateIncidentAsync(
                "mediaforge-api-key-invalid",
                !apiKeyValid,
                apiKeyValid ? "Der MediaForge API-Key ist wieder gültig." : "Der MediaForge API-Key ist ungültig.",
                apiKeyValid ? "The MediaForge API key is valid again." : "The MediaForge API key is invalid.",
                cancellationToken).ConfigureAwait(false);
            if (!healthy)
            {
                throw new MediaForgeBridgeException("MediaForge reported an unhealthy state.");
            }
        }

        foreach (var link in await _database.ListLinksAsync(cancellationToken).ConfigureAwait(false))
        {
            var user = _userManager.GetUserById(link.JellyfinUserId);
            if (user is null)
            {
                continue;
            }

            using var response = await _bridge.InvokeAsync("list", link.JellyfinUserId, user.Username, null, cancellationToken).ConfigureAwait(false);
            if (!response.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                throw new MediaForgeBridgeException("MediaForge returned no request list.");
            }

            foreach (var item in items.EnumerateArray().Take(500))
            {
                await ObserveRequestAsync(link, item, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ObserveRequestAsync(UserLink link, JsonElement item, CancellationToken cancellationToken)
    {
        var requestId = ReadString(item, "id");
        var status = ReadString(item, "status").ToLowerInvariant();
        if (requestId.Length is 0 or > 128 || status.Length is 0 or > 64)
        {
            return;
        }

        var stateKey = $"mediaforge:{link.JellyfinUserId:N}:{requestId}";
        var previous = await _database.GetNotificationStateAsync(stateKey, cancellationToken).ConfigureAwait(false);
        if (previous is null || string.Equals(previous, status, StringComparison.Ordinal) || !IsNotifiable(status))
        {
            await _database.TrySetNotificationStateAsync(stateKey, status, cancellationToken).ConfigureAwait(false);
            return;
        }

        var title = Escape(Limit(ReadString(item, "title"), 200));
        var isFailure = status is "failed" or "rejected";
        var description = status switch
        {
            "available" or "completed" => (Plugin.Instance?.Configuration.Language == "en"
                ? $"**{title}** was added to Jellyfin."
                : $"**{title}** wurde zu Jellyfin hinzugefügt."),
            "rejected" => (Plugin.Instance?.Configuration.Language == "en"
                ? $"**{title}** was rejected."
                : $"**{title}** wurde abgelehnt."),
            _ => (Plugin.Instance?.Configuration.Language == "en"
                ? $"The download for **{title}** failed."
                : $"Der Download für **{title}** ist fehlgeschlagen."),
        };
        var destination = ResolveDestination(link);
        if (destination is not null)
        {
            var payload = JsonSerializer.Serialize(new
            {
                title = isFailure ? "⚠️ MediaForge" : "🎉 " + (Plugin.Instance?.Configuration.Language == "en" ? "Your request is available!" : "Deine Anfrage ist verfügbar!"),
                description,
                color = isFailure ? 0xE74C3Cu : 0x2ECC71u,
            });
            await _database.EnqueueNotificationAsync(NotificationPriority.Normal, "mediaforge-request", destination, payload, $"mediaforge:{link.JellyfinUserId:N}:{requestId}:{status}", DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _database.WriteAuditAsync("bot", "jellix", isFailure ? "request-failed" : "request-available", "mediaforge-request", requestId, true, title, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogCheckFailure(_logger, exception);
        }
        if (status == "failed")
        {
            await QueueAdminAlertAsync("Ein MediaForge-Download ist fehlgeschlagen: " + title, "A MediaForge download failed: " + title, $"download:{link.JellyfinUserId:N}:{requestId}:{status}", cancellationToken).ConfigureAwait(false);
        }

        await _database.TrySetNotificationStateAsync(stateKey, status, cancellationToken).ConfigureAwait(false);
    }

    private static string? ResolveDestination(UserLink link)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.RequestNotificationMode == "off") return null;
        if (config.RequestNotificationMode == "channel" && ulong.TryParse(config.RequestNotificationChannelId, out _)) return "channel:" + config.RequestNotificationChannelId;
        return "dm:" + link.DiscordUserId;
    }

    private async Task UpdateIncidentAsync(string key, bool active, string german, string english, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.AdminAlertsEnabled != true || _discord.ResolveOwnerDestination() is null)
        {
            return;
        }

        if (!await _database.SetHealthIncidentAsync(key, active, DateTime.UtcNow, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var message = config.Language == "en" ? english : german;
        await QueueAdminAlertAsync(message, message, $"health:{key}:{active}:{DateTime.UtcNow:yyyyMMddHHmmss}", cancellationToken, active ? 0xE74C3Cu : 0x2ECC71u).ConfigureAwait(false);
    }

    private async Task QueueAdminAlertAsync(string german, string english, string dedupe, CancellationToken cancellationToken, uint color = 0xE74C3C)
    {
        var config = Plugin.Instance?.Configuration;
        var destination = _discord.ResolveOwnerDestination();
        if (config?.AdminAlertsEnabled != true || destination is null) return;
        var payload = JsonSerializer.Serialize(new { title = "Jellix", description = config.Language == "en" ? english : german, color });
        await _database.EnqueueNotificationAsync(NotificationPriority.High, "admin-alert", destination, payload, dedupe, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    private static string ReadString(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) ? value.ToString().Trim() : string.Empty;

    private static bool IsNotifiable(string status)
        => status is "available" or "completed" or "failed" or "rejected";

    private static string Limit(string value, int length)
        => value.Length <= length ? value : value[..(length - 1)] + "…";

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal).Replace("~", "\\~", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);
}
