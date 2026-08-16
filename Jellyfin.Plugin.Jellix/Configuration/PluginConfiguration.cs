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

    public bool MediaForgeEnabled { get; set; }

    public int MediaForgePollSeconds { get; set; } = 60;

    public string RequestNotificationMode { get; set; } = "dm";

    public string RequestNotificationChannelId { get; set; } = string.Empty;

    public bool NewMediaNotificationsEnabled { get; set; }

    public bool NewEpisodeNotificationsEnabled { get; set; }

    public string NewMediaChannelId { get; set; } = string.Empty;

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

    public string AdminAlertChannelId { get; set; } = string.Empty;

    public int HealthCheckMinutes { get; set; } = 5;

    public bool CheckJellyfinUpdates { get; set; }

    public int AuditRetentionDays { get; set; } = 180;

    public string TimeZoneId { get; set; } = string.Empty;

    public bool UserPageEnabled { get; set; } = true;
}
