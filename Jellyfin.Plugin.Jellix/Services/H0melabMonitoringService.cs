using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Discord;
using Jellyfin.Plugin.Jellix.Integrations;
using Jellyfin.Plugin.Jellix.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellix.Services;

/// <summary>Observes the H0melab connector without becoming a second request data source.</summary>
public sealed class H0melabMonitoringService : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogCheckFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(3101, "H0melabMonitorFailed"),
        "Jellix H0melab monitoring failed");
    private readonly H0melabBridgeClient _bridge;
    private readonly JellixDatabase _database;
    private readonly IUserManager _userManager;
    private readonly DiscordBotService _discord;
    private readonly ILogger<H0melabMonitoringService> _logger;

    public H0melabMonitoringService(
        H0melabBridgeClient bridge,
        JellixDatabase database,
        IUserManager userManager,
        DiscordBotService discord,
        ILogger<H0melabMonitoringService> logger)
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
            if (config?.H0melabEnabled == true)
            {
                try
                {
                    await PollAsync(stoppingToken).ConfigureAwait(false);
                    await UpdateIncidentAsync("h0melab-unreachable", false, "H0melab wieder erreichbar.", "H0melab is reachable again.", stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is H0melabBridgeException or H0melabBridgeUnavailableException)
                {
                    LogCheckFailure(_logger, exception);
                    await UpdateIncidentAsync("h0melab-unreachable", true, "H0melab ist nicht erreichbar oder inkompatibel.", "H0melab is unavailable or incompatible.", stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogCheckFailure(_logger, exception);
                }
            }

            var seconds = Math.Clamp(config?.H0melabPollSeconds ?? 60, 15, 3600);
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        if (!_bridge.IsAvailable)
        {
            throw new H0melabBridgeUnavailableException("H0melab Jellix bridge missing.");
        }

        using (var status = await _bridge.InvokeAsync("status", Guid.Empty, "Jellix", null, cancellationToken).ConfigureAwait(false))
        {
            var root = status.RootElement;
            if (!root.TryGetProperty("healthy", out var healthyValue) || healthyValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("apiKeyValid", out var keyValue) || keyValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new H0melabBridgeException("H0melab returned an invalid status response.");
            }

            var healthy = healthyValue.GetBoolean();
            var apiKeyValid = keyValue.GetBoolean();
            await UpdateIncidentAsync(
                "h0melab-api-key-invalid",
                !apiKeyValid,
                apiKeyValid ? "Der H0melab API-Key ist wieder gültig." : "Der H0melab API-Key ist ungültig.",
                apiKeyValid ? "The H0melab API key is valid again." : "The H0melab API key is invalid.",
                cancellationToken).ConfigureAwait(false);
            if (!healthy)
            {
                throw new H0melabBridgeException("H0melab reported an unhealthy state.");
            }
        }

        // Only links of the configured Discord server: a stale link from another server
        // must not route someone else's result to that Discord account.
        var guildId = Plugin.Instance?.Configuration.GuildId;
        foreach (var link in (await _database.ListLinksAsync(cancellationToken).ConfigureAwait(false)).Where(link => link.GuildId == guildId))
        {
            var user = _userManager.GetUserById(link.JellyfinUserId);
            if (user is null || user.HasPermission(PermissionKind.IsDisabled))
            {
                continue;
            }

            try
            {
                using var response = await _bridge.InvokeAsync("list", link.JellyfinUserId, user.Username, null, cancellationToken).ConfigureAwait(false);
                if (!response.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    throw new H0melabBridgeException("H0melab returned no request list.");
                }

                foreach (var item in items.EnumerateArray().Take(500))
                {
                    await ObserveRequestAsync(link, user.Username, item, cancellationToken).ConfigureAwait(false);
                }
            }
            // A deleted/disabled user or rejected private list must not starve other linked users.
            catch (Exception exception) when (exception is H0melabBridgeException or H0melabBridgeUnavailableException)
            { LogCheckFailure(_logger, exception); }
        }
    }

    private async Task ObserveRequestAsync(UserLink link, string username, JsonElement item, CancellationToken cancellationToken)
    {
        var requestId = ReadString(item, "id");
        var status = ReadString(item, "status").ToLowerInvariant();
        if (requestId.Length is 0 or > 128 || status.Length is 0 or > 64)
        {
            return;
        }

        // The connector lists only the caller's own requests (web page and Discord alike).
        // Bind each request ID to the first Jellyfin user it was seen for, so a result can
        // never be delivered to anyone but the requester even if a listing were broader.
        var ownerKey = "h0melab-owner:" + requestId;
        var owner = await _database.GetNotificationStateAsync(ownerKey, cancellationToken).ConfigureAwait(false);
        if (owner is null) await _database.TrySetNotificationStateAsync(ownerKey, link.JellyfinUserId.ToString("N"), cancellationToken).ConfigureAwait(false);
        else if (!string.Equals(owner, link.JellyfinUserId.ToString("N"), StringComparison.Ordinal)) return;

        var stateKey = $"h0melab:{link.JellyfinUserId:N}:{requestId}";
        var previous = await _database.GetNotificationStateAsync(stateKey, cancellationToken).ConfigureAwait(false);
        if (previous is null || string.Equals(previous, status, StringComparison.Ordinal) || !IsNotifiable(status))
        {
            await _database.TrySetNotificationStateAsync(stateKey, status, cancellationToken).ConfigureAwait(false);
            return;
        }

        var title = Escape(Limit(ReadString(item, "title"), 200));
        var isFailure = status is "failed" or "rejected";
        var english = Plugin.Instance?.Configuration.Language == "en";
        var description = status switch
        {
            "available" or "completed" => english
                ? $"Your request **{title}** is now available on Jellyfin. Enjoy!"
                : $"Deine Anfrage **{title}** ist jetzt auf Jellyfin verfügbar. Viel Spaß!",
            "rejected" => english
                ? $"Your request **{title}** was rejected."
                : $"Deine Anfrage **{title}** wurde abgelehnt.",
            _ => english
                ? $"Your request **{title}** could not be completed."
                : $"Deine Anfrage **{title}** konnte nicht abgeschlossen werden.",
        };
        var destination = ResolveDestination(link);
        if (destination is not null)
        {
            var payload = JsonSerializer.Serialize(new
            {
                title = isFailure
                    ? "⚠️ " + (english ? "Request update" : "Update zu deiner Anfrage")
                    : "🎉 " + (english ? "Your request is available!" : "Deine Anfrage ist verfügbar!"),
                // Naming the requesting Jellyfin account makes a wrong Discord link obvious to the recipient.
                description = description + "\n\n" + (english ? "Requested by Jellyfin account: " : "Angefragt mit Jellyfin-Konto: ") + "**" + Escape(Limit(username, 64)) + "**",
                color = isFailure ? 0xE74C3Cu : 0x2ECC71u,
            });
            await _database.EnqueueNotificationAsync(NotificationPriority.Normal, "h0melab-request", destination, payload, $"h0melab:{link.JellyfinUserId:N}:{requestId}:{status}", DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _database.WriteAuditAsync("bot", "jellix", isFailure ? "request-failed" : "request-completed", "h0melab-request", requestId, true, title, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogCheckFailure(_logger, exception);
        }
        if (status == "failed")
        {
            await QueueAdminAlertAsync("Ein H0melab-Download ist fehlgeschlagen: " + title, "A H0melab download failed: " + title, $"h0melab-download:{link.JellyfinUserId:N}:{requestId}:{status}", cancellationToken).ConfigureAwait(false);
        }

        await _database.TrySetNotificationStateAsync(stateKey, status, cancellationToken).ConfigureAwait(false);
    }

    private static string? ResolveDestination(UserLink link)
    {
        return ResolveRequestDestination(link, Plugin.Instance?.Configuration.RequestNotificationMode);
    }

    internal static string? ResolveRequestDestination(UserLink link, string? mode)
    {
        if (mode is null or "off") return null;
        // A connector request belongs to one linked user. Never fall back to a
        // public channel, because that can disclose a private request and route
        // the result away from the requester.
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
