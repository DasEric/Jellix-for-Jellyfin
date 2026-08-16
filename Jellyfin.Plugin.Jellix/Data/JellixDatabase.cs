using System.Globalization;
using Jellyfin.Plugin.Jellix.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Jellix.Data;

/// <summary>Transactional persistent state owned by Jellix.</summary>
public sealed class JellixDatabase : IDisposable
{
    private const int CurrentSchemaVersion = 4;
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private volatile bool _initialized;

    public JellixDatabase()
        : this(Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("Jellix plugin data path is unavailable."))
    {
    }

    internal JellixDatabase(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _databasePath = Path.Combine(dataPath, "jellix.db");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        _connectionString = builder.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenRawAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, SchemaSql, cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "PlaybackSessions", "NightWatchSeconds", "INTEGER NOT NULL DEFAULT 0 CHECK(NightWatchSeconds >= 0)", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "PlaybackSessions", "CompletedUtc", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "AccessRequests", "DecisionReason", "TEXT NULL", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Playback_UserCompleted ON PlaybackSessions(JellyfinUserId, CompletedUtc);", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "UPDATE PlaybackSessions SET CompletedUtc=COALESCE(EndedUtc, LastEventUtc) WHERE Completed=1 AND CompletedUtc IS NULL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "INSERT OR IGNORE INTO PlaybackSegments(SessionKey, CumulativeWatchSeconds, EventUtc, WatchSeconds) SELECT SessionKey, ActualWatchSeconds, LastEventUtc, ActualWatchSeconds FROM PlaybackSessions WHERE ActualWatchSeconds>0;", cancellationToken).ConfigureAwait(false);

            await using var version = connection.CreateCommand();
            version.CommandText = "INSERT OR IGNORE INTO SchemaMigrations(Version, AppliedUtc) VALUES ($version, $utc);";
            version.Parameters.AddWithValue("$version", CurrentSchemaVersion);
            version.Parameters.AddWithValue("$utc", FormatUtc(DateTime.UtcNow));
            await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            RestrictDatabaseFiles();
            _initialized = true;
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    public async Task<UserLink?> FindLinkByDiscordAsync(string guildId, string discordUserId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GuildId, DiscordUserId, JellyfinUserId, LinkedUtc, LinkedBy FROM DiscordUserLinks WHERE GuildId=$guild AND DiscordUserId=$discord AND Active=1;";
        command.Parameters.AddWithValue("$guild", guildId);
        command.Parameters.AddWithValue("$discord", discordUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLink(reader) : null;
    }

    public async Task<UserLink?> FindLinkByJellyfinAsync(Guid jellyfinUserId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GuildId, DiscordUserId, JellyfinUserId, LinkedUtc, LinkedBy FROM DiscordUserLinks WHERE JellyfinUserId=$jellyfin AND Active=1;";
        command.Parameters.AddWithValue("$jellyfin", jellyfinUserId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLink(reader) : null;
    }

    public async Task<IReadOnlyList<UserLink>> ListLinksAsync(CancellationToken cancellationToken)
    {
        var result = new List<UserLink>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GuildId, DiscordUserId, JellyfinUserId, LinkedUtc, LinkedBy FROM DiscordUserLinks WHERE Active=1 ORDER BY LinkedUtc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadLink(reader));
        }

        return result;
    }

    public async Task LinkUserAsync(string guildId, string discordUserId, Guid jellyfinUserId, string linkedBy, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO DiscordUserLinks(GuildId, DiscordUserId, JellyfinUserId, LinkedUtc, LinkedBy, Active)
            VALUES ($guild, $discord, $jellyfin, $utc, $by, 1);
            """;
        command.Parameters.AddWithValue("$guild", guildId);
        command.Parameters.AddWithValue("$discord", discordUserId);
        command.Parameters.AddWithValue("$jellyfin", jellyfinUserId.ToString("N"));
        command.Parameters.AddWithValue("$utc", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$by", linkedBy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var privacy = connection.CreateCommand();
        privacy.Transaction = (SqliteTransaction)transaction;
        privacy.CommandText = "INSERT OR IGNORE INTO UserPrivacy(JellyfinUserId, ShowInLeaderboard, ShowNamePublicly, ShowNowPlaying, AnnounceAchievements) VALUES ($id, 0, 0, 0, 1);";
        privacy.Parameters.AddWithValue("$id", jellyfinUserId.ToString("N"));
        await privacy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UnlinkUserAsync(string guildId, string discordUserId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DiscordUserLinks WHERE GuildId=$guild AND DiscordUserId=$discord;";
        command.Parameters.AddWithValue("$guild", guildId);
        command.Parameters.AddWithValue("$discord", discordUserId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceLinkCodeAsync(Guid jellyfinUserId, byte[] codeHash, DateTime expiresUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM LinkCodes WHERE JellyfinUserId=$id OR ExpiresUtc <= $now;";
            delete.Parameters.AddWithValue("$id", jellyfinUserId.ToString("N"));
            delete.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO LinkCodes(CodeHash, JellyfinUserId, CreatedUtc, ExpiresUtc) VALUES ($hash, $id, $now, $expires);";
            insert.Parameters.AddWithValue("$hash", codeHash);
            insert.Parameters.AddWithValue("$id", jellyfinUserId.ToString("N"));
            insert.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
            insert.Parameters.AddWithValue("$expires", FormatUtc(expiresUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid?> ConsumeLinkCodeAsync(byte[] codeHash, string guildId, string discordUserId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string? jellyfinId = null;
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = (SqliteTransaction)transaction;
            find.CommandText = "SELECT JellyfinUserId FROM LinkCodes WHERE CodeHash=$hash AND ExpiresUtc>$now;";
            find.Parameters.AddWithValue("$hash", codeHash);
            find.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
            jellyfinId = (string?)await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        if (jellyfinId is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await using (var link = connection.CreateCommand())
        {
            link.Transaction = (SqliteTransaction)transaction;
            link.CommandText = """
                INSERT INTO DiscordUserLinks(GuildId, DiscordUserId, JellyfinUserId, LinkedUtc, LinkedBy, Active)
                VALUES ($guild, $discord, $jellyfin, $utc, 'self-link', 1);
                """;
            link.Parameters.AddWithValue("$guild", guildId);
            link.Parameters.AddWithValue("$discord", discordUserId);
            link.Parameters.AddWithValue("$jellyfin", jellyfinId);
            link.Parameters.AddWithValue("$utc", FormatUtc(DateTime.UtcNow));
            await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM LinkCodes WHERE CodeHash=$hash OR JellyfinUserId=$jellyfin;";
            delete.Parameters.AddWithValue("$hash", codeHash);
            delete.Parameters.AddWithValue("$jellyfin", jellyfinId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var privacy = connection.CreateCommand())
        {
            privacy.Transaction = (SqliteTransaction)transaction;
            privacy.CommandText = "INSERT OR IGNORE INTO UserPrivacy(JellyfinUserId, ShowInLeaderboard, ShowNamePublicly, ShowNowPlaying, AnnounceAchievements) VALUES ($id, 0, 0, 0, 1);";
            privacy.Parameters.AddWithValue("$id", jellyfinId);
            await privacy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Guid.Parse(jellyfinId);
    }

    public async Task<UserPrivacy> GetPrivacyAsync(Guid jellyfinUserId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ShowInLeaderboard, ShowNamePublicly, ShowNowPlaying, AnnounceAchievements FROM UserPrivacy WHERE JellyfinUserId=$id;";
        command.Parameters.AddWithValue("$id", jellyfinUserId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new UserPrivacy(jellyfinUserId, false, false, false, true);
        }

        return new UserPrivacy(jellyfinUserId, reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3));
    }

    public async Task SetPrivacyAsync(UserPrivacy value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UserPrivacy(JellyfinUserId, ShowInLeaderboard, ShowNamePublicly, ShowNowPlaying, AnnounceAchievements)
            VALUES ($id, $leaderboard, $name, $playing, $achievements)
            ON CONFLICT(JellyfinUserId) DO UPDATE SET
              ShowInLeaderboard=excluded.ShowInLeaderboard,
              ShowNamePublicly=excluded.ShowNamePublicly,
              ShowNowPlaying=excluded.ShowNowPlaying,
              AnnounceAchievements=excluded.AnnounceAchievements;
            """;
        command.Parameters.AddWithValue("$id", value.JellyfinUserId.ToString("N"));
        command.Parameters.AddWithValue("$leaderboard", value.ShowInLeaderboard);
        command.Parameters.AddWithValue("$name", value.ShowNamePublicly);
        command.Parameters.AddWithValue("$playing", value.ShowNowPlaying);
        command.Parameters.AddWithValue("$achievements", value.AnnounceAchievements);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertPlaybackAsync(PlaybackRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long previousSeconds;
        await using (var previous = connection.CreateCommand())
        {
            previous.Transaction = (SqliteTransaction)transaction;
            previous.CommandText = "SELECT ActualWatchSeconds FROM PlaybackSessions WHERE SessionKey=$key;";
            previous.Parameters.AddWithValue("$key", record.SessionKey);
            previousSeconds = Convert.ToInt64(await previous.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L, CultureInfo.InvariantCulture);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO PlaybackSessions(SessionKey, JellyfinUserId, ItemId, ItemType, SeriesId, ItemName, SeriesName,
              StartedUtc, EndedUtc, ActualWatchSeconds, NightWatchSeconds, RuntimeTicks, LastPositionTicks, LastEventUtc, Completed, CompletedUtc, DeviceName)
            VALUES ($key, $user, $item, $type, $series, $itemName, $seriesName, $started, $ended, $seconds, $nightSeconds,
              $runtime, $position, $event, $completed, $completedUtc, $device)
            ON CONFLICT(SessionKey) DO UPDATE SET
              EndedUtc=CASE WHEN excluded.LastEventUtc >= PlaybackSessions.LastEventUtc THEN COALESCE(excluded.EndedUtc, PlaybackSessions.EndedUtc) ELSE PlaybackSessions.EndedUtc END,
              ActualWatchSeconds=MAX(PlaybackSessions.ActualWatchSeconds, excluded.ActualWatchSeconds),
              NightWatchSeconds=MAX(PlaybackSessions.NightWatchSeconds, excluded.NightWatchSeconds),
              LastPositionTicks=CASE WHEN excluded.LastEventUtc >= PlaybackSessions.LastEventUtc THEN excluded.LastPositionTicks ELSE PlaybackSessions.LastPositionTicks END,
              LastEventUtc=MAX(PlaybackSessions.LastEventUtc, excluded.LastEventUtc),
              Completed=MAX(PlaybackSessions.Completed, excluded.Completed),
              CompletedUtc=COALESCE(PlaybackSessions.CompletedUtc, excluded.CompletedUtc),
              DeviceName=CASE WHEN excluded.LastEventUtc >= PlaybackSessions.LastEventUtc THEN excluded.DeviceName ELSE PlaybackSessions.DeviceName END;
            """;
        command.Parameters.AddWithValue("$key", record.SessionKey);
        command.Parameters.AddWithValue("$user", record.JellyfinUserId.ToString("N"));
        command.Parameters.AddWithValue("$item", record.ItemId.ToString("N"));
        command.Parameters.AddWithValue("$type", record.ItemType);
        command.Parameters.AddWithValue("$series", record.SeriesId?.ToString("N") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$itemName", record.ItemName);
        command.Parameters.AddWithValue("$seriesName", record.SeriesName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$started", FormatUtc(record.StartedUtc));
        command.Parameters.AddWithValue("$ended", record.EndedUtc.HasValue ? FormatUtc(record.EndedUtc.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$seconds", record.ActualWatchSeconds);
        command.Parameters.AddWithValue("$nightSeconds", record.NightWatchSeconds);
        command.Parameters.AddWithValue("$runtime", record.RuntimeTicks);
        command.Parameters.AddWithValue("$position", record.LastPositionTicks);
        command.Parameters.AddWithValue("$event", FormatUtc(record.LastEventUtc));
        command.Parameters.AddWithValue("$completed", record.Completed);
        command.Parameters.AddWithValue("$completedUtc", record.CompletedUtc.HasValue ? FormatUtc(record.CompletedUtc.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$device", record.DeviceName);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var delta = record.ActualWatchSeconds - previousSeconds;
        if (delta > 0)
        {
            await using var segment = connection.CreateCommand();
            segment.Transaction = (SqliteTransaction)transaction;
            segment.CommandText = "INSERT OR IGNORE INTO PlaybackSegments(SessionKey, CumulativeWatchSeconds, EventUtc, WatchSeconds) VALUES ($key, $cumulative, $event, $seconds);";
            segment.Parameters.AddWithValue("$key", record.SessionKey);
            segment.Parameters.AddWithValue("$cumulative", record.ActualWatchSeconds);
            segment.Parameters.AddWithValue("$event", FormatUtc(record.LastEventUtc));
            segment.Parameters.AddWithValue("$seconds", delta);
            await segment.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StatisticsSummary> GetStatisticsAsync(Guid userId, DateTime? sinceUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var completionWhere = "JellyfinUserId=$user" + (sinceUtc.HasValue ? " AND CompletedUtc >= $since" : string.Empty);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
              COUNT(DISTINCT CASE WHEN ItemType='Movie' AND Completed=1 THEN ItemId END),
              COUNT(DISTINCT CASE WHEN ItemType='Episode' AND Completed=1 THEN SeriesId END),
              COUNT(DISTINCT CASE WHEN ItemType='Episode' AND Completed=1 THEN ItemId END)
            FROM PlaybackSessions WHERE {completionWhere};
            """;
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        command.Parameters.AddWithValue("$since", sinceUtc.HasValue ? FormatUtc(sinceUtc.Value) : DBNull.Value);

        long movies;
        long series;
        long episodes;
        long seconds;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            movies = reader.GetInt64(0);
            series = reader.GetInt64(1);
            episodes = reader.GetInt64(2);
        }

        await using (var watch = connection.CreateCommand())
        {
            watch.CommandText = "SELECT COALESCE(SUM(s.WatchSeconds), 0) FROM PlaybackSegments s INNER JOIN PlaybackSessions p ON p.SessionKey=s.SessionKey WHERE p.JellyfinUserId=$user AND ($since IS NULL OR s.EventUtc >= $since);";
            watch.Parameters.AddWithValue("$user", userId.ToString("N"));
            watch.Parameters.AddWithValue("$since", sinceUtc.HasValue ? FormatUtc(sinceUtc.Value) : DBNull.Value);
            seconds = Convert.ToInt64(await watch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        var currentSeries = await ScalarStringAsync(connection,
            "SELECT SeriesName FROM PlaybackSessions WHERE JellyfinUserId=$user AND ($since IS NULL OR LastEventUtc >= $since) AND ItemType='Episode' AND SeriesName IS NOT NULL ORDER BY LastEventUtc DESC LIMIT 1;",
            userId, sinceUtc, cancellationToken).ConfigureAwait(false);
        var topSeries = await ScalarStringAsync(connection,
            "SELECT p.SeriesName FROM PlaybackSegments s INNER JOIN PlaybackSessions p ON p.SessionKey=s.SessionKey WHERE p.JellyfinUserId=$user AND ($since IS NULL OR s.EventUtc >= $since) AND p.ItemType='Episode' AND p.SeriesName IS NOT NULL GROUP BY p.SeriesId, p.SeriesName ORDER BY SUM(s.WatchSeconds) DESC LIMIT 1;",
            userId, sinceUtc, cancellationToken).ConfigureAwait(false);
        return new StatisticsSummary(movies, series, episodes, seconds, currentSeries, topSeries);
    }

    public async Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(string category, DateTime? sinceUtc, int limit, CancellationToken cancellationToken)
    {
        var expression = category switch
        {
            "movies" => "COUNT(DISTINCT CASE WHEN p.ItemType='Movie' AND p.Completed=1 AND ($since IS NULL OR p.CompletedUtc >= $since) THEN p.ItemId END)",
            "series" => "COUNT(DISTINCT CASE WHEN p.ItemType='Episode' AND p.Completed=1 AND ($since IS NULL OR p.CompletedUtc >= $since) THEN p.SeriesId END)",
            "episodes" => "COUNT(DISTINCT CASE WHEN p.ItemType='Episode' AND p.Completed=1 AND ($since IS NULL OR p.CompletedUtc >= $since) THEN p.ItemId END)",
            _ => "COALESCE(SUM(CASE WHEN $since IS NULL OR s.EventUtc >= $since THEN s.WatchSeconds ELSE 0 END), 0)",
        };
        var result = new List<LeaderboardEntry>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.JellyfinUserId, {expression} AS Value
            FROM PlaybackSessions p
            INNER JOIN UserPrivacy u ON u.JellyfinUserId=p.JellyfinUserId AND u.ShowInLeaderboard=1
            LEFT JOIN PlaybackSegments s ON s.SessionKey=p.SessionKey
            GROUP BY p.JellyfinUserId HAVING Value > 0
            ORDER BY Value DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$since", sinceUtc.HasValue ? FormatUtc(sinceUtc.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new LeaderboardEntry(Guid.Parse(reader.GetString(0)), reader.GetInt64(1)));
        }

        return result;
    }

    public async Task<bool> UnlockAchievementAsync(Guid userId, string achievementId, DateTime unlockedUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO UserAchievements(JellyfinUserId, AchievementId, UnlockedUtc, NotificationSent) VALUES ($user, $achievement, $utc, 0);";
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        command.Parameters.AddWithValue("$achievement", achievementId);
        command.Parameters.AddWithValue("$utc", FormatUtc(unlockedUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<AchievementMetrics> GetAchievementMetricsAsync(Guid userId, TimeZoneInfo timeZone, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long movies;
        long episodes;
        long watchSeconds;
        long nightSeconds = 0;
        var episodesPerDay = new Dictionary<DateOnly, HashSet<string>>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ItemId, ItemType, COALESCE(CompletedUtc, EndedUtc, LastEventUtc, StartedUtc), ActualWatchSeconds, NightWatchSeconds, Completed FROM PlaybackSessions WHERE JellyfinUserId=$user;";
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        var movieIds = new HashSet<string>(StringComparer.Ordinal);
        var episodeIds = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        watchSeconds = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var itemId = reader.GetString(0);
            var itemType = reader.GetString(1);
            var startedUtc = ParseUtc(reader.GetString(2));
            var seconds = reader.GetInt64(3);
            var sessionNightSeconds = reader.GetInt64(4);
            var completed = reader.GetBoolean(5);
            watchSeconds += seconds;
            var local = TimeZoneInfo.ConvertTimeFromUtc(startedUtc, timeZone);
            nightSeconds += sessionNightSeconds;

            if (!completed)
            {
                continue;
            }

            if (itemType == "Movie")
            {
                movieIds.Add(itemId);
            }
            else if (itemType == "Episode")
            {
                episodeIds.Add(itemId);
                var day = DateOnly.FromDateTime(local);
                if (!episodesPerDay.TryGetValue(day, out var ids))
                {
                    ids = new HashSet<string>(StringComparer.Ordinal);
                    episodesPerDay[day] = ids;
                }

                ids.Add(itemId);
            }
        }

        movies = movieIds.Count;
        episodes = episodeIds.Count;
        return new AchievementMetrics(movies, episodes, watchSeconds, nightSeconds, episodesPerDay.Values.Select(value => (long)value.Count).DefaultIfEmpty().Max());
    }

    internal static double CalculateNightSeconds(DateTime startedUtc, double watchSeconds, TimeZoneInfo timeZone)
    {
        if (watchSeconds <= 0) return 0;
        var sessionStart = startedUtc.ToUniversalTime();
        var sessionEnd = sessionStart.AddSeconds(watchSeconds);
        var firstLocalDay = TimeZoneInfo.ConvertTimeFromUtc(sessionStart, timeZone).Date.AddDays(-1);
        var lastLocalDay = TimeZoneInfo.ConvertTimeFromUtc(sessionEnd, timeZone).Date;
        double result = 0;
        for (var day = firstLocalDay; day <= lastLocalDay; day = day.AddDays(1))
        {
            var nightStart = ConvertLocalToUtc(day, timeZone);
            var nightEnd = ConvertLocalToUtc(day.AddHours(5), timeZone);
            var overlapStart = sessionStart > nightStart ? sessionStart : nightStart;
            var overlapEnd = sessionEnd < nightEnd ? sessionEnd : nightEnd;
            if (overlapEnd > overlapStart) result += (overlapEnd - overlapStart).TotalSeconds;
        }

        return result;
    }

    private static DateTime ConvertLocalToUtc(DateTime value, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }

    public async Task CreatePasswordTicketAsync(byte[] tokenHash, Guid jellyfinUserId, string discordUserId, DateTime expiresUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var remove = connection.CreateCommand())
        {
            remove.Transaction = (SqliteTransaction)transaction;
            remove.CommandText = "DELETE FROM PasswordTickets WHERE JellyfinUserId=$user OR ExpiresUtc<=$now OR Used=1;";
            remove.Parameters.AddWithValue("$user", jellyfinUserId.ToString("N"));
            remove.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
            await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO PasswordTickets(TokenHash, JellyfinUserId, DiscordUserId, ExpiresUtc, Used) VALUES ($hash, $user, $discord, $expires, 0);";
            insert.Parameters.AddWithValue("$hash", tokenHash);
            insert.Parameters.AddWithValue("$user", jellyfinUserId.ToString("N"));
            insert.Parameters.AddWithValue("$discord", discordUserId);
            insert.Parameters.AddWithValue("$expires", FormatUtc(expiresUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PasswordTicket?> GetPasswordTicketAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT JellyfinUserId, DiscordUserId, ExpiresUtc FROM PasswordTickets WHERE TokenHash=$hash AND Used=0 AND ExpiresUtc>$now;";
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PasswordTicket(Guid.Parse(reader.GetString(0)), reader.GetString(1), ParseUtc(reader.GetString(2)))
            : null;
    }

    public async Task<bool> ConsumePasswordTicketAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PasswordTickets SET Used=1 WHERE TokenHash=$hash AND Used=0 AND ExpiresUtc>$now;";
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<long> CreateAccessRequestAsync(string guildId, string discordUserId, string requestedName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO AccessRequests(GuildId, DiscordUserId, RequestedName, Status, CreatedUtc) VALUES ($guild, $discord, $name, 'pending', $utc); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$guild", guildId);
        command.Parameters.AddWithValue("$discord", discordUserId);
        command.Parameters.AddWithValue("$name", requestedName);
        command.Parameters.AddWithValue("$utc", FormatUtc(DateTime.UtcNow));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    public async Task<AccessRequestRecord?> GetAccessRequestAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, GuildId, DiscordUserId, RequestedName, Status, CreatedUtc, DecidedUtc, DecidedBy, DecisionReason FROM AccessRequests WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAccessRequest(reader) : null;
    }

    public async Task<bool> DecideAccessRequestAsync(long id, string status, string decidedBy, string? reason, CancellationToken cancellationToken)
    {
        if (status is not ("approved" or "rejected"))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        reason = reason?.Trim();
        if (reason?.Length > 500) reason = reason[..500];
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AccessRequests SET Status=$status, DecidedUtc=$utc, DecidedBy=$by, DecisionReason=$reason WHERE Id=$id AND Status='pending';";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$utc", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$by", decidedBy);
        command.Parameters.AddWithValue("$reason", string.IsNullOrWhiteSpace(reason) ? DBNull.Value : reason);
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<DateTime?> GetLastAccessDecisionUtcAsync(string guildId, string discordUserId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DecidedUtc FROM AccessRequests WHERE GuildId=$guild AND DiscordUserId=$discord AND Status='rejected' ORDER BY Id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$guild", guildId);
        command.Parameters.AddWithValue("$discord", discordUserId);
        var value = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : ParseUtc(value);
    }

    public async Task CancelPendingAccessRequestAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AccessRequests WHERE Id=$id AND Status='pending';";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnqueueNotificationAsync(NotificationPriority priority, string kind, string destination, string payloadJson, string dedupeKey, DateTime availableUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO NotificationOutbox(Priority, Kind, Destination, PayloadJson, DedupeKey, Attempts, AvailableUtc) VALUES ($priority, $kind, $destination, $payload, $dedupe, 0, $available);";
        command.Parameters.AddWithValue("$priority", (int)priority);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$destination", destination);
        command.Parameters.AddWithValue("$payload", payloadJson);
        command.Parameters.AddWithValue("$dedupe", dedupeKey);
        command.Parameters.AddWithValue("$available", FormatUtc(availableUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationJob?> GetNextNotificationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Priority, Kind, Destination, PayloadJson, DedupeKey, Attempts, AvailableUtc FROM NotificationOutbox WHERE CompletedUtc IS NULL AND AvailableUtc<=$now ORDER BY Priority, Id LIMIT 1;";
        command.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new NotificationJob(reader.GetInt64(0), (NotificationPriority)reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6), ParseUtc(reader.GetString(7)))
            : null;
    }

    public async Task CompleteNotificationAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE NotificationOutbox SET CompletedUtc=$utc, LastError=NULL WHERE Id=$id;";
        command.Parameters.AddWithValue("$utc", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RetryNotificationAsync(long id, int attempts, DateTime availableUtc, string error, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE NotificationOutbox SET Attempts=$attempts, AvailableUtc=$available, LastError=$error WHERE Id=$id;";
        command.Parameters.AddWithValue("$attempts", attempts);
        command.Parameters.AddWithValue("$available", FormatUtc(availableUtc));
        command.Parameters.AddWithValue("$error", error.Length > 200 ? error[..200] : error);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AbandonNotificationAsync(long id, string error, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE NotificationOutbox SET CompletedUtc=$utc, LastError=$error WHERE Id=$id;";
        command.Parameters.AddWithValue("$utc", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$error", error.Length > 200 ? error[..200] : error);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TrySetNotificationStateAsync(string stateKey, string value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO NotificationState(StateKey, Value, UpdatedUtc) VALUES ($key, $value, $utc) ON CONFLICT(StateKey) DO UPDATE SET Value=excluded.Value, UpdatedUtc=excluded.UpdatedUtc WHERE NotificationState.Value<>excluded.Value;";
        command.Parameters.AddWithValue("$key", stateKey);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$utc", FormatUtc(DateTime.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<string?> GetNotificationStateAsync(string stateKey, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM NotificationState WHERE StateKey=$key;";
        command.Parameters.AddWithValue("$key", stateKey);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecoverInterruptedPlaybackAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PlaybackSessions SET EndedUtc=LastEventUtc WHERE EndedUtc IS NULL AND LastEventUtc<$cutoff;";
        command.Parameters.AddWithValue("$cutoff", FormatUtc(DateTime.UtcNow.AddMinutes(-10)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListAchievementsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT AchievementId FROM UserAchievements WHERE JellyfinUserId=$user ORDER BY UnlockedUtc;";
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> ListPendingAchievementNotificationsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT AchievementId FROM UserAchievements WHERE JellyfinUserId=$user AND NotificationSent=0 ORDER BY UnlockedUtc;";
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task MarkAchievementNotificationHandledAsync(Guid userId, string achievementId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE UserAchievements SET NotificationSent=1 WHERE JellyfinUserId=$user AND AchievementId=$achievement;";
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        command.Parameters.AddWithValue("$achievement", achievementId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertStickyAsync(StickyMessageRecord sticky, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO StickyMessages(GuildId, ChannelId, SourceMessageId, CurrentMessageId, ContentJson, CreatedByDiscordUserId, Enabled, LastRepostedUtc)
            VALUES ($guild, $channel, $source, $current, $content, $createdBy, $enabled, $last)
            ON CONFLICT(GuildId, ChannelId) DO UPDATE SET SourceMessageId=excluded.SourceMessageId,
              CurrentMessageId=excluded.CurrentMessageId, ContentJson=excluded.ContentJson,
              CreatedByDiscordUserId=excluded.CreatedByDiscordUserId, Enabled=excluded.Enabled,
              LastRepostedUtc=excluded.LastRepostedUtc;
            """;
        command.Parameters.AddWithValue("$guild", sticky.GuildId);
        command.Parameters.AddWithValue("$channel", sticky.ChannelId);
        command.Parameters.AddWithValue("$source", sticky.SourceMessageId);
        command.Parameters.AddWithValue("$current", sticky.CurrentMessageId);
        command.Parameters.AddWithValue("$content", sticky.ContentJson);
        command.Parameters.AddWithValue("$createdBy", sticky.CreatedByDiscordUserId);
        command.Parameters.AddWithValue("$enabled", sticky.Enabled);
        command.Parameters.AddWithValue("$last", sticky.LastRepostedUtc.HasValue ? FormatUtc(sticky.LastRepostedUtc.Value) : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StickyMessageRecord?> GetStickyAsync(string guildId, string channelId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GuildId, ChannelId, SourceMessageId, CurrentMessageId, ContentJson, CreatedByDiscordUserId, Enabled, LastRepostedUtc FROM StickyMessages WHERE GuildId=$guild AND ChannelId=$channel;";
        command.Parameters.AddWithValue("$guild", guildId);
        command.Parameters.AddWithValue("$channel", channelId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StickyMessageRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)));
    }

    public async Task<IReadOnlyList<StickyMessageRecord>> ListStickiesAsync(CancellationToken cancellationToken)
    {
        var result = new List<StickyMessageRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GuildId, ChannelId, SourceMessageId, CurrentMessageId, ContentJson, CreatedByDiscordUserId, Enabled, LastRepostedUtc FROM StickyMessages WHERE Enabled=1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new StickyMessageRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7))));
        }

        return result;
    }

    public async Task DeleteStickyAsync(string guildId, string channelId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM StickyMessages WHERE GuildId=$guild AND ChannelId=$channel;";
        command.Parameters.AddWithValue("$guild", guildId);
        command.Parameters.AddWithValue("$channel", channelId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAuditAsync(string actorType, string actorId, string action, string targetType, string targetId, bool success, string details, CancellationToken cancellationToken)
    {
        details = details.Length > 500 ? details[..500] : details;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO AuditLog(CreatedUtc, ActorType, ActorId, Action, TargetType, TargetId, Success, Details) VALUES ($utc, $actorType, $actorId, $action, $targetType, $targetId, $success, $details);";
        command.Parameters.AddWithValue("$utc", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$actorType", actorType);
        command.Parameters.AddWithValue("$actorId", actorId);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$targetType", targetType);
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$success", success);
        command.Parameters.AddWithValue("$details", details);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditRecord>> ListAuditAsync(int limit, long beforeId, CancellationToken cancellationToken)
    {
        var result = new List<AuditRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, CreatedUtc, ActorType, ActorId, Action, TargetType, TargetId, Success, Details FROM AuditLog WHERE ($before=0 OR Id<$before) ORDER BY Id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$before", Math.Max(0, beforeId));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new AuditRecord(reader.GetInt64(0), ParseUtc(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetBoolean(7), reader.GetString(8)));
        }

        return result;
    }

    public async Task PruneAsync(int auditRetentionDays, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM LinkCodes WHERE ExpiresUtc <= $now;
            DELETE FROM PasswordTickets WHERE ExpiresUtc <= $now OR Used=1;
            DELETE FROM AuditLog WHERE CreatedUtc < $auditCutoff;
            DELETE FROM NotificationOutbox WHERE CompletedUtc IS NOT NULL AND CompletedUtc < $outboxCutoff;
            """;
        command.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$auditCutoff", FormatUtc(DateTime.UtcNow.AddDays(-Math.Clamp(auditRetentionDays, 1, 3650))));
        command.Parameters.AddWithValue("$outboxCutoff", FormatUtc(DateTime.UtcNow.AddDays(-30)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetBotStateAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO BotState(Key, Value, UpdatedUtc) VALUES ($key, $value, $utc) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value, UpdatedUtc=excluded.UpdatedUtc;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$utc", FormatUtc(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetBotStateAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM BotState WHERE Key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryRegisterLibraryItemAsync(Guid itemId, DateTime firstSeenUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO LibraryAnnouncementState(ItemId, FirstSeenUtc) VALUES ($item, $utc); SELECT changes();";
        command.Parameters.AddWithValue("$item", itemId.ToString("N"));
        command.Parameters.AddWithValue("$utc", FormatUtc(firstSeenUtc));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    public async Task SeedLibraryItemsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT OR IGNORE INTO LibraryAnnouncementState(ItemId, FirstSeenUtc, AnnouncedUtc) VALUES ($item, $utc, $utc);";
        var itemParameter = command.Parameters.Add("$item", SqliteType.Text);
        command.Parameters.AddWithValue("$utc", FormatUtc(DateTime.UtcNow));
        foreach (var itemId in itemIds.Distinct())
        {
            itemParameter.Value = itemId.ToString("N");
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> ListPendingLibraryAnnouncementsAsync(CancellationToken cancellationToken)
    {
        var result = new List<Guid>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ItemId FROM LibraryAnnouncementState WHERE AnnouncedUtc IS NULL ORDER BY FirstSeenUtc LIMIT 1000;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Guid.TryParse(reader.GetString(0), out var id)) result.Add(id);
        }

        return result;
    }

    public async Task<bool> IsLibraryItemPendingAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM LibraryAnnouncementState WHERE ItemId=$item AND AnnouncedUtc IS NULL;";
        command.Parameters.AddWithValue("$item", itemId.ToString("N"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<bool> IsLibraryItemKnownAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM LibraryAnnouncementState WHERE ItemId=$item;";
        command.Parameters.AddWithValue("$item", itemId.ToString("N"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task MarkLibraryItemAnnouncedAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LibraryAnnouncementState SET AnnouncedUtc=$utc WHERE ItemId=$item AND AnnouncedUtc IS NULL;";
        command.Parameters.AddWithValue("$item", itemId.ToString("N"));
        command.Parameters.AddWithValue("$utc", FormatUtc(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkLegacyLibraryBaselineAnnouncedAsync(DateTime baselineUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LibraryAnnouncementState SET AnnouncedUtc=FirstSeenUtc WHERE AnnouncedUtc IS NULL AND FirstSeenUtc<=$baseline;";
        command.Parameters.AddWithValue("$baseline", FormatUtc(baselineUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetHealthIncidentAsync(string incidentKey, bool active, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.Transaction = (SqliteTransaction)transaction;
        read.CommandText = "SELECT Active FROM HealthIncidentState WHERE IncidentKey=$key;";
        read.Parameters.AddWithValue("$key", incidentKey);
        var current = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var changed = current is null
            ? active
            : Convert.ToInt64(current, CultureInfo.InvariantCulture) != (active ? 1 : 0);

        await using var write = connection.CreateCommand();
        write.Transaction = (SqliteTransaction)transaction;
        write.CommandText = """
            INSERT INTO HealthIncidentState(IncidentKey, Active, FirstSeenUtc, LastSeenUtc, LastNotifiedUtc)
            VALUES ($key, $active, $now, $now, CASE WHEN $changed=1 THEN $now ELSE NULL END)
            ON CONFLICT(IncidentKey) DO UPDATE SET
              Active=excluded.Active,
              FirstSeenUtc=CASE WHEN HealthIncidentState.Active<>excluded.Active THEN excluded.FirstSeenUtc ELSE HealthIncidentState.FirstSeenUtc END,
              LastSeenUtc=excluded.LastSeenUtc,
              LastNotifiedUtc=CASE WHEN HealthIncidentState.Active<>excluded.Active THEN excluded.LastNotifiedUtc ELSE HealthIncidentState.LastNotifiedUtc END;
            """;
        write.Parameters.AddWithValue("$key", incidentKey);
        write.Parameters.AddWithValue("$active", active ? 1 : 0);
        write.Parameters.AddWithValue("$changed", changed ? 1 : 0);
        write.Parameters.AddWithValue("$now", FormatUtc(nowUtc));
        await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<long> GetPendingNotificationCountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM NotificationOutbox WHERE CompletedUtc IS NULL;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var connection = await OpenRawAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        RestrictDatabaseFiles();
        return connection;
    }

    private void RestrictDatabaseFiles()
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await ExecuteAsync(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", cancellationToken).ConfigureAwait(false);
    }

    private static UserLink ReadLink(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), Guid.Parse(reader.GetString(2)), ParseUtc(reader.GetString(3)), reader.GetString(4));

    private static AccessRequestRecord ReadAccessRequest(SqliteDataReader reader)
        => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), ParseUtc(reader.GetString(5)), reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8));

    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, string sql, Guid userId, DateTime? sinceUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        command.Parameters.AddWithValue("$since", sinceUtc.HasValue ? FormatUtc(sinceUtc.Value) : DBNull.Value);

        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FormatUtc(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    public void Dispose()
    {
        _migrationLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS SchemaMigrations(
          Version INTEGER PRIMARY KEY,
          AppliedUtc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS DiscordUserLinks(
          GuildId TEXT NOT NULL,
          DiscordUserId TEXT NOT NULL,
          JellyfinUserId TEXT NOT NULL,
          LinkedUtc TEXT NOT NULL,
          LinkedBy TEXT NOT NULL,
          Active INTEGER NOT NULL DEFAULT 1 CHECK(Active IN (0,1)),
          PRIMARY KEY(GuildId, DiscordUserId),
          UNIQUE(JellyfinUserId)
        );
        CREATE TABLE IF NOT EXISTS LinkCodes(
          CodeHash BLOB PRIMARY KEY,
          JellyfinUserId TEXT NOT NULL UNIQUE,
          CreatedUtc TEXT NOT NULL,
          ExpiresUtc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS PasswordTickets(
          TokenHash BLOB PRIMARY KEY,
          JellyfinUserId TEXT NOT NULL,
          DiscordUserId TEXT NOT NULL,
          ExpiresUtc TEXT NOT NULL,
          Used INTEGER NOT NULL DEFAULT 0 CHECK(Used IN (0,1))
        );
        CREATE TABLE IF NOT EXISTS PlaybackSessions(
          SessionKey TEXT PRIMARY KEY,
          JellyfinUserId TEXT NOT NULL,
          ItemId TEXT NOT NULL,
          ItemType TEXT NOT NULL,
          SeriesId TEXT NULL,
          ItemName TEXT NOT NULL,
          SeriesName TEXT NULL,
          StartedUtc TEXT NOT NULL,
          EndedUtc TEXT NULL,
          ActualWatchSeconds INTEGER NOT NULL DEFAULT 0 CHECK(ActualWatchSeconds >= 0),
          NightWatchSeconds INTEGER NOT NULL DEFAULT 0 CHECK(NightWatchSeconds >= 0),
          RuntimeTicks INTEGER NOT NULL DEFAULT 0 CHECK(RuntimeTicks >= 0),
          LastPositionTicks INTEGER NOT NULL DEFAULT 0 CHECK(LastPositionTicks >= 0),
          LastEventUtc TEXT NOT NULL,
          Completed INTEGER NOT NULL DEFAULT 0 CHECK(Completed IN (0,1)),
          CompletedUtc TEXT NULL,
          DeviceName TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS IX_Playback_UserStarted ON PlaybackSessions(JellyfinUserId, StartedUtc);
        CREATE INDEX IF NOT EXISTS IX_Playback_UserItem ON PlaybackSessions(JellyfinUserId, ItemId);
        CREATE TABLE IF NOT EXISTS PlaybackSegments(
          SessionKey TEXT NOT NULL,
          CumulativeWatchSeconds INTEGER NOT NULL CHECK(CumulativeWatchSeconds > 0),
          EventUtc TEXT NOT NULL,
          WatchSeconds INTEGER NOT NULL CHECK(WatchSeconds > 0),
          PRIMARY KEY(SessionKey, CumulativeWatchSeconds),
          FOREIGN KEY(SessionKey) REFERENCES PlaybackSessions(SessionKey) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS IX_PlaybackSegments_Event ON PlaybackSegments(EventUtc);
        CREATE TABLE IF NOT EXISTS UserPrivacy(
          JellyfinUserId TEXT PRIMARY KEY,
          ShowInLeaderboard INTEGER NOT NULL DEFAULT 0 CHECK(ShowInLeaderboard IN (0,1)),
          ShowNamePublicly INTEGER NOT NULL DEFAULT 0 CHECK(ShowNamePublicly IN (0,1)),
          ShowNowPlaying INTEGER NOT NULL DEFAULT 0 CHECK(ShowNowPlaying IN (0,1)),
          AnnounceAchievements INTEGER NOT NULL DEFAULT 1 CHECK(AnnounceAchievements IN (0,1))
        );
        CREATE TABLE IF NOT EXISTS UserAchievements(
          JellyfinUserId TEXT NOT NULL,
          AchievementId TEXT NOT NULL,
          UnlockedUtc TEXT NOT NULL,
          NotificationSent INTEGER NOT NULL DEFAULT 0 CHECK(NotificationSent IN (0,1)),
          PRIMARY KEY(JellyfinUserId, AchievementId)
        );
        CREATE TABLE IF NOT EXISTS StickyMessages(
          GuildId TEXT NOT NULL,
          ChannelId TEXT NOT NULL,
          SourceMessageId TEXT NOT NULL,
          CurrentMessageId TEXT NOT NULL,
          ContentJson TEXT NOT NULL,
          CreatedByDiscordUserId TEXT NOT NULL,
          Enabled INTEGER NOT NULL DEFAULT 1 CHECK(Enabled IN (0,1)),
          LastRepostedUtc TEXT NULL,
          PRIMARY KEY(GuildId, ChannelId)
        );
        CREATE TABLE IF NOT EXISTS AccessRequests(
          Id INTEGER PRIMARY KEY AUTOINCREMENT,
          GuildId TEXT NOT NULL,
          DiscordUserId TEXT NOT NULL,
          RequestedName TEXT NOT NULL,
          Status TEXT NOT NULL,
          CreatedUtc TEXT NOT NULL,
          DecidedUtc TEXT NULL,
          DecidedBy TEXT NULL,
          DecisionReason TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_AccessRequests_Open ON AccessRequests(GuildId, DiscordUserId) WHERE Status='pending';
        CREATE TABLE IF NOT EXISTS AuditLog(
          Id INTEGER PRIMARY KEY AUTOINCREMENT,
          CreatedUtc TEXT NOT NULL,
          ActorType TEXT NOT NULL,
          ActorId TEXT NOT NULL,
          Action TEXT NOT NULL,
          TargetType TEXT NOT NULL,
          TargetId TEXT NOT NULL,
          Success INTEGER NOT NULL CHECK(Success IN (0,1)),
          Details TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS IX_Audit_Created ON AuditLog(CreatedUtc);
        CREATE TABLE IF NOT EXISTS NotificationState(
          StateKey TEXT PRIMARY KEY,
          Value TEXT NOT NULL,
          UpdatedUtc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS NotificationOutbox(
          Id INTEGER PRIMARY KEY AUTOINCREMENT,
          Priority INTEGER NOT NULL,
          Kind TEXT NOT NULL,
          Destination TEXT NOT NULL,
          PayloadJson TEXT NOT NULL,
          DedupeKey TEXT NOT NULL UNIQUE,
          Attempts INTEGER NOT NULL DEFAULT 0,
          AvailableUtc TEXT NOT NULL,
          CompletedUtc TEXT NULL,
          LastError TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS BotState(
          Key TEXT PRIMARY KEY,
          Value TEXT NOT NULL,
          UpdatedUtc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS LibraryAnnouncementState(
          ItemId TEXT PRIMARY KEY,
          AnnouncedUtc TEXT NULL,
          FirstSeenUtc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS HealthIncidentState(
          IncidentKey TEXT PRIMARY KEY,
          Active INTEGER NOT NULL CHECK(Active IN (0,1)),
          FirstSeenUtc TEXT NOT NULL,
          LastSeenUtc TEXT NOT NULL,
          LastNotifiedUtc TEXT NULL
        );
        """;
}
