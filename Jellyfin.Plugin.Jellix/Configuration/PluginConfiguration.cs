using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellix.Configuration;

/// <summary>Static Jellix settings persisted by Jellyfin.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool BotEnabled { get; set; }

    public string GuildId { get; set; } = string.Empty;

    public string JellyfinPublicUrl { get; set; } = string.Empty;

    public string Language { get; set; } = "de";

    public string StreamingRoleId { get; set; } = string.Empty;

    public string RequestRoleId { get; set; } = string.Empty;

    public string AdminRoleId { get; set; } = string.Empty;

    public bool SelfLinkEnabled { get; set; } = true;

    public int LinkCodeLifetimeMinutes { get; set; } = 5;

    public bool PasswordChangeEnabled { get; set; } = true;

    public int PasswordTicketLifetimeMinutes { get; set; } = 10;

    public bool RevokeSessionsAfterPasswordChange { get; set; } = true;

    public bool UnlockAccountEnabled { get; set; }

    public bool StatisticsEnabled { get; set; } = true;

    public int CompletedPlaybackPercent { get; set; } = 90;

    public bool LeaderboardEnabled { get; set; }

    public bool AchievementsEnabled { get; set; } = true;

    public bool AchievementFilmFanEnabled { get; set; } = true;

    public bool AchievementCineasteEnabled { get; set; } = true;

    public bool AchievementSeriesJunkieEnabled { get; set; } = true;

    public bool AchievementNightOwlEnabled { get; set; } = true;

    public bool AchievementBingeWatcherEnabled { get; set; } = true;

    public bool AchievementNoLifeEnabled { get; set; } = true;

    public string AchievementChannelId { get; set; } = string.Empty;

    public string AchievementNotificationMode { get; set; } = "dm";

    public bool H0melabEnabled { get; set; }

    public int H0melabPollSeconds { get; set; } = 60;

    // Serialization aliases retain existing settings, but never activate the old bridge.
    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public bool MediaForgeEnabled { get => H0melabEnabled; set => H0melabEnabled = value; }

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public int MediaForgePollSeconds { get => H0melabPollSeconds; set => H0melabPollSeconds = value; }

    public string RequestNotificationMode { get; set; } = "dm";

    public string RequestNotificationChannelId { get; set; } = string.Empty;

    public bool NewMediaNotificationsEnabled { get; set; }

    public bool NewEpisodeNotificationsEnabled { get; set; }

    public string NewMediaChannelId { get; set; } = string.Empty;

    public int SeriesQuietSeconds { get; set; } = 120;

    public int SeriesMergeHours { get; set; } = 24;

    public bool LibraryOverviewEnabled { get; set; }

    public string LibraryOverviewChannelId { get; set; } = string.Empty;

    public int LibraryOverviewMinutes { get; set; } = 5;

    /// <summary>Comma-separated Jellyfin library IDs; empty selects all movie/TV/mixed libraries.</summary>
    public string LibraryIds { get; set; } = string.Empty;

    /// <summary>Optional newline-separated storage paths, as visible inside the Jellyfin process.</summary>
    public string MediaStoragePaths { get; set; } = string.Empty;

    public string NowPlayingMode { get; set; } = "admin";

    public bool NowPlayingShowUsernames { get; set; }

    public bool RandomEnabled { get; set; } = true;

    public bool AccessRequestsEnabled { get; set; }

    public string AccessRequestChannelId { get; set; } = string.Empty;

    public int AccessRequestCooldownHours { get; set; } = 72;

    public bool AssignStreamingRoleAfterApproval { get; set; } = true;

    public bool StickyEnabled { get; set; } = true;

    public int StickyDebounceSeconds { get; set; } = 2;

    public bool AdminAlertsEnabled { get; set; }

    public string AdminAlertMode { get; set; } = "owner-dm";

    public string AdminAlertChannelId { get; set; } = string.Empty;

    public int HealthCheckMinutes { get; set; } = 5;

    public bool CheckJellyfinUpdates { get; set; }

    public int AuditRetentionDays { get; set; } = 180;

    public string TimeZoneId { get; set; } = string.Empty;

    public bool UserPageEnabled { get; set; } = true;
}
