using System.Text.Json;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellix.Services;

public sealed class AchievementService
{
    private static readonly Action<ILogger, Exception?> LogAuditFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(3201, "AchievementAuditFailed"),
        "Jellix could not write an achievement audit entry");
    private readonly JellixDatabase _database;
    private readonly IUserManager _userManager;
    private readonly ILogger<AchievementService> _logger;

    public AchievementService(JellixDatabase database, IUserManager userManager, ILogger<AchievementService> logger)
    {
        _database = database;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task EvaluateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.AchievementsEnabled != true)
        {
            return;
        }

        var timeZone = ResolveTimeZone(config.TimeZoneId);
        var metrics = await _database.GetAchievementMetricsAsync(userId, timeZone, cancellationToken).ConfigureAwait(false);
        var candidates = new[]
        {
            new Candidate("film-fan", "🍿 Filmfan", Text("50 Filme angesehen", "50 movies watched"), config.AchievementFilmFanEnabled && metrics.Movies >= 50),
            new Candidate("cineaste", "🎬 Cineast", Text("250 Filme angesehen", "250 movies watched"), config.AchievementCineasteEnabled && metrics.Movies >= 250),
            new Candidate("series-junkie", Text("📺 Serienjunkie", "📺 Series junkie"), Text("500 Episoden angesehen", "500 episodes watched"), config.AchievementSeriesJunkieEnabled && metrics.Episodes >= 500),
            new Candidate("night-owl", Text("🌙 Nachteule", "🌙 Night owl"), Text("50 Stunden zwischen 00:00 und 05:00 Uhr geschaut", "50 hours watched between midnight and 5 a.m."), config.AchievementNightOwlEnabled && metrics.NightSeconds >= 50 * 3600),
            new Candidate("binge-watcher", "🔥 Binge Watcher", Text("10 Episoden an einem Tag angesehen", "10 episodes watched in one day"), config.AchievementBingeWatcherEnabled && metrics.MaxEpisodesInLocalDay >= 10),
            new Candidate("no-life", "💀 No Life", Text("1.000 Stunden Watchtime", "1,000 hours of watch time"), config.AchievementNoLifeEnabled && metrics.WatchSeconds >= 1000 * 3600),
        };

        var byId = candidates.ToDictionary(static value => value.Id, StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(value => value.Unlocked))
        {
            if (!await _database.UnlockAchievementAsync(userId, candidate.Id, DateTime.UtcNow, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                await _database.WriteAuditAsync("bot", "jellix", "achievement-unlocked", "jellyfin-user", userId.ToString("N"), true, candidate.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogAuditFailure(_logger, exception);
            }
        }

        foreach (var achievementId in await _database.ListPendingAchievementNotificationsAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            if (!byId.TryGetValue(achievementId, out var candidate))
            {
                await _database.MarkAchievementNotificationHandledAsync(userId, achievementId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await QueueNotificationAsync(userId, candidate, cancellationToken).ConfigureAwait(false);
            await _database.MarkAchievementNotificationHandledAsync(userId, candidate.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task QueueNotificationAsync(Guid userId, Candidate achievement, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var privacy = await _database.GetPrivacyAsync(userId, cancellationToken).ConfigureAwait(false);
        if (config is null || !privacy.AnnounceAchievements || config.AchievementNotificationMode == "off")
        {
            return;
        }

        string destination;
        string notificationTitle;
        string notificationDescription;
        if (config.AchievementNotificationMode == "channel" && ulong.TryParse(config.AchievementChannelId, out _))
        {
            destination = "channel:" + config.AchievementChannelId;
            var user = _userManager.GetUserById(userId);
            var displayName = privacy.ShowNamePublicly && user is not null ? Escape(Limit(user.Username, 100)) : Text("Ein Benutzer", "A user");
            notificationTitle = Text($"🏆 {displayName} hat einen Erfolg freigeschaltet!", $"🏆 {displayName} unlocked an achievement!");
            notificationDescription = $"**{achievement.Name}**\n{achievement.Description}";
        }
        else
        {
            var link = await _database.FindLinkByJellyfinAsync(userId, cancellationToken).ConfigureAwait(false);
            if (link is null)
            {
                return;
            }

            destination = "dm:" + link.DiscordUserId;
            notificationTitle = achievement.Name;
            notificationDescription = achievement.Description;
        }

        var payload = JsonSerializer.Serialize(new
        {
            title = notificationTitle,
            description = notificationDescription,
            color = 0xF1C40F,
        });
        await _database.EnqueueNotificationAsync(NotificationPriority.Normal, "achievement", destination, payload, $"achievement:{userId:N}:{achievement.Id}", DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Fall back to the server's local zone.
            }
            catch (InvalidTimeZoneException)
            {
                // Fall back to the server's local zone.
            }
        }

        return TimeZoneInfo.Local;
    }

    private sealed record Candidate(string Id, string Name, string Description, bool Unlocked);

    private static string Text(string german, string english)
        => Plugin.Instance?.Configuration.Language == "en" ? english : german;

    private static string Limit(string value, int length)
        => value.Length <= length ? value : value[..(length - 1)] + "…";

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal).Replace("~", "\\~", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);
}
