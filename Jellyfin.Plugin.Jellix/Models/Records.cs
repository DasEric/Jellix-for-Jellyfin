namespace Jellyfin.Plugin.Jellix.Models;

public sealed record UserLink(
    string GuildId,
    string DiscordUserId,
    Guid JellyfinUserId,
    DateTime LinkedUtc,
    string LinkedBy);

public sealed record UserPrivacy(
    Guid JellyfinUserId,
    bool ShowInLeaderboard,
    bool ShowNamePublicly,
    bool ShowNowPlaying,
    bool AnnounceAchievements);

public sealed record PlaybackRecord(
    string SessionKey,
    Guid JellyfinUserId,
    Guid ItemId,
    string ItemType,
    Guid? SeriesId,
    string ItemName,
    string? SeriesName,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    long ActualWatchSeconds,
    long NightWatchSeconds,
    long RuntimeTicks,
    long LastPositionTicks,
    DateTime LastEventUtc,
    bool Completed,
    DateTime? CompletedUtc,
    string DeviceName);

public sealed record StatisticsSummary(
    long MovieCount,
    long SeriesCount,
    long EpisodeCount,
    long WatchSeconds,
    string? CurrentSeries,
    string? TopSeries);

public sealed record AchievementMetrics(
    long Movies,
    long Episodes,
    long WatchSeconds,
    long NightSeconds,
    long MaxEpisodesInLocalDay);

public sealed record LeaderboardEntry(
    Guid JellyfinUserId,
    long Value);

public sealed record StickyMessageRecord(
    string GuildId,
    string ChannelId,
    string SourceMessageId,
    string CurrentMessageId,
    string ContentJson,
    string CreatedByDiscordUserId,
    bool Enabled,
    DateTime? LastRepostedUtc);

public sealed record AccessRequestRecord(
    long Id,
    string GuildId,
    string DiscordUserId,
    string RequestedName,
    string Status,
    DateTime CreatedUtc,
    DateTime? DecidedUtc,
    string? DecidedBy);

public sealed record AuditRecord(
    long Id,
    DateTime CreatedUtc,
    string ActorType,
    string ActorId,
    string Action,
    string TargetType,
    string TargetId,
    bool Success,
    string Details);

public sealed record PasswordTicket(
    Guid JellyfinUserId,
    string DiscordUserId,
    DateTime ExpiresUtc);

public enum NotificationPriority
{
    High = 0,
    Normal = 1,
    Low = 2,
}

public sealed record NotificationJob(
    long Id,
    NotificationPriority Priority,
    string Kind,
    string Destination,
    string PayloadJson,
    string DedupeKey,
    int Attempts,
    DateTime AvailableUtc);
