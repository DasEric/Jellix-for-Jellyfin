using System.Text;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Discord;
using Jellyfin.Plugin.Jellix.Helpers;
using Jellyfin.Plugin.Jellix.Integrations;
using Jellyfin.Plugin.Jellix.Models;
using Jellyfin.Plugin.Jellix.Security;
using Jellyfin.Plugin.Jellix.Services;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Data.Sqlite;

SQLitePCL.Batteries_V2.Init();
var tests = new (string Name, Func<Task> Run)[]
{
    ("link code hashing is normalized", TestLinkHashAsync),
    ("secret storage is encrypted", TestSecretStoreAsync),
    ("web injection is idempotent", TestWebInjectionAsync),
    ("Jellyfin 12 menu link patch is safe", TestWebConfigMenuLinkAsync),
    ("public password URLs reject unsafe components", TestPublicUrlAsync),
    ("database preserves one-to-one links and state", TestDatabaseAsync),
    ("database migrates legacy playback data", TestDatabaseMigrationAsync),
    ("playback accounting ignores pauses and seeks", TestPlaybackAsync),
    ("broken optional MediaForge bridge stays isolated", TestMediaForgeIsolationAsync),
    ("request results always resolve to requester DM", TestRequestDestinationAsync),
    ("library batches suppress child episode spam", TestLibraryHierarchyAsync),
    ("sticky ignores only its own repost", TestStickyTriggerAsync),
};
var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run().ConfigureAwait(false);
        Console.WriteLine("PASS " + test.Name);
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine("FAIL " + test.Name + ": " + exception.Message);
    }
}

return failed == 0 ? 0 : 1;

static Task TestLinkHashAsync()
{
    Equal(Convert.ToHexString(AccountLinkService.HashCode("7F3K-92MX")), Convert.ToHexString(AccountLinkService.HashCode("7f3k 92mx")));
    NotEqual(Convert.ToHexString(AccountLinkService.HashCode("7F3K-92MX")), Convert.ToHexString(AccountLinkService.HashCode("7F3K-92MY")));
    return Task.CompletedTask;
}

static Task TestSecretStoreAsync()
{
    var path = TempDirectory();
    try
    {
        const string token = "never-store-this-discord-token-in-plaintext";
        var store = new SecretStore(path);
        store.SetToken(token);
        Equal(token, store.GetToken());
        foreach (var file in Directory.GetFiles(path))
        {
            False(Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(token, StringComparison.Ordinal));
        }

        File.WriteAllBytes(Path.Combine(path, "jellix-secret.key"), [1, 2, 3]);
        Equal<string?>(null, store.GetToken());
        store.SetToken("replacement-token");
        Equal("replacement-token", store.GetToken());

        store.ClearToken();
        False(store.HasToken);
        False(File.Exists(Path.Combine(path, "jellix-secret.key")));
    }
    finally
    {
        Directory.Delete(path, true);
    }

    return Task.CompletedTask;
}

static Task TestPublicUrlAsync()
{
    True(PasswordTicketService.TryGetPublicBaseUri("https://example.test/jellyfin", out var uri));
    Equal("/jellyfin", uri.AbsolutePath);
    True(PasswordTicketService.TryGetPublicBaseUri("http://127.0.0.1:8096", out _));
    False(PasswordTicketService.TryGetPublicBaseUri("http://example.test", out _));
    False(PasswordTicketService.TryGetPublicBaseUri("https://user:pass@example.test", out _));
    False(PasswordTicketService.TryGetPublicBaseUri("https://example.test/?redirect=evil", out _));
    False(PasswordTicketService.TryGetPublicBaseUri("https://example.test/#fragment", out _));
    return Task.CompletedTask;
}

static Task TestWebInjectionAsync()
{
    const string original = "<html><body>Jellyfin</body></html>";
    var once = TransformationPatches.ApplyIndexHtml(original, true);
    var twice = TransformationPatches.ApplyIndexHtml(once, true);
    Equal(once, twice);
    Equal(original, TransformationPatches.ApplyIndexHtml(twice, false));
    return Task.CompletedTask;
}

static async Task TestDatabaseAsync()
{
    var path = TempDirectory();
    try
    {
        using var database = new JellixDatabase(path);
        await database.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var user = Guid.NewGuid();
        await database.LinkUserAsync("123456789012345678", "223456789012345678", user, "test", CancellationToken.None).ConfigureAwait(false);
        var link = await database.FindLinkByDiscordAsync("123456789012345678", "223456789012345678", CancellationToken.None).ConfigureAwait(false);
        Equal(user, link?.JellyfinUserId);
        var duplicateRejected = false;
        try
        {
            await database.LinkUserAsync("123456789012345678", "323456789012345678", user, "test", CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            duplicateRejected = true;
        }

        True(duplicateRejected);
        await database.UnlinkUserAsync("123456789012345678", "223456789012345678", CancellationToken.None).ConfigureAwait(false);
        await database.LinkUserAsync("123456789012345678", "323456789012345678", user, "test", CancellationToken.None).ConfigureAwait(false);
        True(await database.FindLinkByDiscordAsync("123456789012345678", "323456789012345678", CancellationToken.None).ConfigureAwait(false) is not null);
        True(await database.TrySetNotificationStateAsync("request:1", "pending", CancellationToken.None).ConfigureAwait(false));
        False(await database.TrySetNotificationStateAsync("request:1", "pending", CancellationToken.None).ConfigureAwait(false));
        True(await database.TrySetNotificationStateAsync("request:1", "available", CancellationToken.None).ConfigureAwait(false));
        await database.EnqueueNotificationAsync(NotificationPriority.Low, "test", "channel:1", "{}", "same", DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
        await database.EnqueueNotificationAsync(NotificationPriority.Low, "test", "channel:1", "{}", "same", DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
        Equal(1L, await database.GetPendingNotificationCountAsync(CancellationToken.None).ConfigureAwait(false));
        var sticky = new StickyMessageRecord("guild", "channel", "source", "current", "{}", "admin", true, DateTime.UtcNow);
        await database.UpsertStickyAsync(sticky, CancellationToken.None).ConfigureAwait(false);
        Equal("source", (await database.GetStickyAsync("guild", "channel", CancellationToken.None).ConfigureAwait(false))?.SourceMessageId);
        False(await database.SetHealthIncidentAsync("mediaforge", false, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false));
        True(await database.SetHealthIncidentAsync("mediaforge", true, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false));
        False(await database.SetHealthIncidentAsync("mediaforge", true, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false));
        True(await database.SetHealthIncidentAsync("mediaforge", false, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false));
        var accessId = await database.CreateAccessRequestAsync("guild", "discord", "Eric", CancellationToken.None).ConfigureAwait(false);
        await database.CancelPendingAccessRequestAsync(accessId, CancellationToken.None).ConfigureAwait(false);
        Equal<AccessRequestRecord?>(null, await database.GetAccessRequestAsync(accessId, CancellationToken.None).ConfigureAwait(false));
        var rejectedId = await database.CreateAccessRequestAsync("guild", "discord", "Eric", CancellationToken.None).ConfigureAwait(false);
        True(await database.DecideAccessRequestAsync(rejectedId, "rejected", "owner", "No capacity available.", CancellationToken.None).ConfigureAwait(false));
        var rejected = await database.GetAccessRequestAsync(rejectedId, CancellationToken.None).ConfigureAwait(false);
        Equal("rejected", rejected?.Status);
        Equal("No capacity available.", rejected?.DecisionReason);
        False(await database.DecideAccessRequestAsync(rejectedId, "approved", "owner", null, CancellationToken.None).ConfigureAwait(false));
        var baselineId = Guid.NewGuid();
        await database.SeedLibraryItemsAsync([baselineId], CancellationToken.None).ConfigureAwait(false);
        False(await database.IsLibraryItemPendingAsync(baselineId, CancellationToken.None).ConfigureAwait(false));
        var pendingId = Guid.NewGuid();
        True(await database.TryRegisterLibraryItemAsync(pendingId, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false));
        True(await database.IsLibraryItemPendingAsync(pendingId, CancellationToken.None).ConfigureAwait(false));
        await database.MarkLibraryItemAnnouncedAsync(pendingId, CancellationToken.None).ConfigureAwait(false);
        False(await database.IsLibraryItemPendingAsync(pendingId, CancellationToken.None).ConfigureAwait(false));
        var identityItem = Guid.NewGuid();
        True(await database.TryRegisterLibraryIdentityAsync("stable-identity", identityItem, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false));
        True(await database.IsLibraryIdentityPendingAsync("stable-identity", CancellationToken.None).ConfigureAwait(false));
        await database.MarkLibraryIdentityAnnouncedAsync("stable-identity", CancellationToken.None).ConfigureAwait(false);
        False(await database.IsLibraryIdentityPendingAsync("stable-identity", CancellationToken.None).ConfigureAwait(false));
        False(await database.TryRegisterLibraryIdentityAsync("stable-identity", Guid.NewGuid(), DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false));
        var playbackUser = Guid.NewGuid();
        var playbackItem = Guid.NewGuid();
        var started = DateTime.UtcNow.AddHours(-2);
        var first = new PlaybackRecord("period-test", playbackUser, playbackItem, "Movie", null, "Movie", null, started, null, 10, 0, TimeSpan.FromHours(2).Ticks, TimeSpan.FromSeconds(10).Ticks, started.AddSeconds(10), false, null, "Test");
        await database.UpsertPlaybackAsync(first, CancellationToken.None).ConfigureAwait(false);
        var boundary = DateTime.UtcNow.AddMinutes(-1);
        var completed = first with { ActualWatchSeconds = 30, LastEventUtc = DateTime.UtcNow, Completed = true, CompletedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow };
        await database.UpsertPlaybackAsync(completed, CancellationToken.None).ConfigureAwait(false);
        var recentStats = await database.GetStatisticsAsync(playbackUser, boundary, CancellationToken.None).ConfigureAwait(false);
        Equal(1L, recentStats.MovieCount);
        Equal(20L, recentStats.WatchSeconds);
        Equal(30L, (await database.GetStatisticsAsync(playbackUser, null, CancellationToken.None).ConfigureAwait(false)).WatchSeconds);
        var stale = first with { ActualWatchSeconds = 5, LastEventUtc = started.AddSeconds(5) };
        await database.UpsertPlaybackAsync(stale, CancellationToken.None).ConfigureAwait(false);
        var afterStaleUpdate = await database.GetStatisticsAsync(playbackUser, null, CancellationToken.None).ConfigureAwait(false);
        Equal(1L, afterStaleUpdate.MovieCount);
        Equal(30L, afterStaleUpdate.WatchSeconds);
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(path, true);
    }
}

static Task TestPlaybackAsync()
{
    var tenSeconds = TimeSpan.FromSeconds(10).Ticks;
    EqualApprox(10d, PlaybackTimeCalculator.AcceptedSeconds(0, tenSeconds, 10, false, false), 0.01);
    EqualApprox(10d, PlaybackTimeCalculator.AcceptedSeconds(0, tenSeconds, 10, false, true), 0.01);
    EqualApprox(10d, PlaybackTimeCalculator.AcceptedSeconds(0, tenSeconds * 2, 10, false, false), 0.01);
    EqualApprox(10d, PlaybackTimeCalculator.AcceptedSeconds(0, tenSeconds / 2, 10, false, false), 0.01);
    EqualApprox(0d, PlaybackTimeCalculator.AcceptedSeconds(0, tenSeconds, 10, true, false), 0.01);
    True(PlaybackTimeCalculator.AcceptedSeconds(0, TimeSpan.FromHours(1).Ticks, 10, false, false) < 20);
    EqualApprox(0d, PlaybackTimeCalculator.AcceptedSeconds(tenSeconds, 0, 10, false, false), 0.01);
    EqualApprox(1800d, JellixDatabase.CalculateNightSeconds(new DateTime(2026, 8, 16, 4, 30, 0, DateTimeKind.Utc), 7200, TimeZoneInfo.Utc), 0.01);
    EqualApprox(0.5d, JellixDatabase.CalculateNightSeconds(new DateTime(2026, 8, 16, 4, 59, 59, 500, DateTimeKind.Utc), 1, TimeZoneInfo.Utc), 0.01);
    return Task.CompletedTask;
}

static async Task TestDatabaseMigrationAsync()
{
    var path = TempDirectory();
    var linkedUser = Guid.NewGuid();
    try
    {
        var databasePath = Path.Combine(path, "jellix.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE PlaybackSessions(
                  SessionKey TEXT PRIMARY KEY, JellyfinUserId TEXT NOT NULL, ItemId TEXT NOT NULL,
                  ItemType TEXT NOT NULL, SeriesId TEXT NULL, ItemName TEXT NOT NULL, SeriesName TEXT NULL,
                  StartedUtc TEXT NOT NULL, EndedUtc TEXT NULL, ActualWatchSeconds INTEGER NOT NULL,
                  RuntimeTicks INTEGER NOT NULL, LastPositionTicks INTEGER NOT NULL, LastEventUtc TEXT NOT NULL,
                  Completed INTEGER NOT NULL, DeviceName TEXT NOT NULL
                );
                INSERT INTO PlaybackSessions VALUES(
                  'legacy', $user, $item, 'Movie', NULL, 'Legacy movie', NULL,
                  $time, $time, 42, 100, 100, $time, 1, 'Legacy device'
                );
                CREATE TABLE AccessRequests(
                  Id INTEGER PRIMARY KEY AUTOINCREMENT, GuildId TEXT NOT NULL, DiscordUserId TEXT NOT NULL,
                  RequestedName TEXT NOT NULL, Status TEXT NOT NULL, CreatedUtc TEXT NOT NULL,
                  DecidedUtc TEXT NULL, DecidedBy TEXT NULL
                );
                INSERT INTO AccessRequests(GuildId, DiscordUserId, RequestedName, Status, CreatedUtc)
                VALUES('guild', 'discord', 'Legacy', 'pending', $time);
                CREATE TABLE DiscordUserLinks(
                  GuildId TEXT NOT NULL, DiscordUserId TEXT NOT NULL, JellyfinUserId TEXT NOT NULL,
                  LinkedUtc TEXT NOT NULL, LinkedBy TEXT NOT NULL, Active INTEGER NOT NULL DEFAULT 1,
                  PRIMARY KEY(GuildId, DiscordUserId), UNIQUE(JellyfinUserId)
                );
                INSERT INTO DiscordUserLinks VALUES('guild', '223456789012345678', $linked, $time, 'legacy', 1);
                CREATE TABLE NotificationOutbox(
                  Id INTEGER PRIMARY KEY AUTOINCREMENT, Priority INTEGER NOT NULL, Kind TEXT NOT NULL,
                  Destination TEXT NOT NULL, PayloadJson TEXT NOT NULL, DedupeKey TEXT NOT NULL UNIQUE,
                  Attempts INTEGER NOT NULL DEFAULT 0, AvailableUtc TEXT NOT NULL,
                  CompletedUtc TEXT NULL, LastError TEXT NULL
                );
                INSERT INTO NotificationOutbox(Priority, Kind, Destination, PayloadJson, DedupeKey, AvailableUtc)
                VALUES(1, 'mediaforge-request', 'channel:123', '{}', 'mediaforge:' || $linked || ':request:available', $time);
                INSERT INTO NotificationOutbox(Priority, Kind, Destination, PayloadJson, DedupeKey, AvailableUtc)
                VALUES(1, 'mediaforge-request', 'channel:123', '{}', 'mediaforge:00000000000000000000000000000000:orphan:available', $time);
                """;
            command.Parameters.AddWithValue("$user", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$item", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$linked", linkedUser.ToString("N"));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using var database = new JellixDatabase(path);
        await database.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await using var verification = new SqliteConnection($"Data Source={databasePath}");
        await verification.OpenAsync().ConfigureAwait(false);
        await using var verify = verification.CreateCommand();
        verify.CommandText = "SELECT NightWatchSeconds, CompletedUtc, (SELECT SUM(WatchSeconds) FROM PlaybackSegments WHERE SessionKey='legacy') FROM PlaybackSessions WHERE SessionKey='legacy';";
        await using var reader = await verify.ExecuteReaderAsync().ConfigureAwait(false);
        True(await reader.ReadAsync().ConfigureAwait(false));
        Equal(0L, reader.GetInt64(0));
        False(reader.IsDBNull(1));
        Equal(42L, reader.GetInt64(2));
        await reader.DisposeAsync().ConfigureAwait(false);
        verify.CommandText = "SELECT DecisionReason FROM AccessRequests WHERE RequestedName='Legacy';";
        var decisionReason = await verify.ExecuteScalarAsync().ConfigureAwait(false);
        True(decisionReason is null or DBNull);
        verify.CommandText = "SELECT Destination FROM NotificationOutbox WHERE DedupeKey LIKE $dedupe;";
        verify.Parameters.Clear();
        verify.Parameters.AddWithValue("$dedupe", $"mediaforge:{linkedUser:N}:%");
        Equal("dm:223456789012345678", (string?)await verify.ExecuteScalarAsync().ConfigureAwait(false));
        verify.CommandText = "SELECT CompletedUtc IS NOT NULL FROM NotificationOutbox WHERE DedupeKey LIKE '%:orphan:available';";
        verify.Parameters.Clear();
        Equal(1L, Convert.ToInt64(await verify.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture));
        verify.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Version=5;";
        Equal(1L, Convert.ToInt64(await verify.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture));
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(path, true);
    }
}

static Task TestMediaForgeIsolationAsync()
{
    var bridge = new MediaForgeBridgeClient(new Jellyfin.Plugin.MediaForge.Integration.ThrowingServiceProvider());
    False(bridge.IsAvailable);
    return Task.CompletedTask;
}

static Task TestWebConfigMenuLinkAsync()
{
    const string original = "{\"multiserver\":true,\"menuLinks\":[{\"name\":\"Existing\",\"icon\":\"home\",\"url\":\"https://example.test\"}]}";
    var enabled = WebConfigMenuLink.ApplyToJson(original, "Discord", "forum", "#/jellix-discord", true);
    True(enabled.Contains("#/jellix-discord", StringComparison.Ordinal));
    True(enabled.Contains("https://example.test", StringComparison.Ordinal));
    Equal(enabled, WebConfigMenuLink.ApplyToJson(enabled, "Discord", "forum", "#/jellix-discord", true));

    var allPlugins = WebConfigMenuLink.ApplyToJson(enabled, "Anfragen", "playlist_add", "#/mediaforge-requests", true);
    allPlugins = WebConfigMenuLink.ApplyToJson(allPlugins, "Theme Store", "palette", "#/theme-store", true);
    var withoutJellix = WebConfigMenuLink.ApplyToJson(allPlugins, "Discord", "forum", "#/jellix-discord", false);
    True(withoutJellix.Contains("#/mediaforge-requests", StringComparison.Ordinal));
    True(withoutJellix.Contains("#/theme-store", StringComparison.Ordinal));

    const string duplicate = "{\"menuLinks\":[{\"name\":\"Old\",\"url\":\"#/jellix-discord\"},{\"name\":\"Old again\",\"url\":\"#/jellix-discord\"}]}";
    var deduplicated = WebConfigMenuLink.ApplyToJson(duplicate, "Discord", "forum", "#/jellix-discord", true);
    Equal(1, deduplicated.Split("#/jellix-discord", StringSplitOptions.None).Length - 1);

    var disabled = WebConfigMenuLink.ApplyToJson(enabled, "Discord", "forum", "#/jellix-discord", false);
    False(disabled.Contains("#/jellix-discord", StringComparison.Ordinal));
    True(disabled.Contains("https://example.test", StringComparison.Ordinal));
    Equal(disabled, WebConfigMenuLink.ApplyToJson(disabled, "Discord", "forum", "#/jellix-discord", false));

    var rejectedInvalidShape = false;
    try
    {
        WebConfigMenuLink.ApplyToJson("{\"menuLinks\":{}}", "Discord", "forum", "#/jellix-discord", true);
    }
    catch (InvalidDataException)
    {
        rejectedInvalidShape = true;
    }

    True(rejectedInvalidShape);

    var directory = TempDirectory();
    try
    {
        var path = Path.Combine(directory, "config.json");
        File.WriteAllText(path, original);
        True(WebConfigMenuLink.UpdateFile(path, "Discord", "forum", "#/jellix-discord", true));
        False(WebConfigMenuLink.UpdateFile(path, "Discord", "forum", "#/jellix-discord", true));
        True(File.ReadAllText(path).Contains("#/jellix-discord", StringComparison.Ordinal));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

static Task TestRequestDestinationAsync()
{
    var link = new UserLink("guild", "223456789012345678", Guid.NewGuid(), DateTime.UtcNow, "test");
    Equal<string?>(null, MediaForgeMonitoringService.ResolveRequestDestination(link, "off"));
    Equal("dm:223456789012345678", MediaForgeMonitoringService.ResolveRequestDestination(link, "dm"));
    Equal("dm:223456789012345678", MediaForgeMonitoringService.ResolveRequestDestination(link, "channel"));
    True(DiscordBotService.ShouldSuppressPersonalRequest("mediaforge-request", "off", "dm:223456789012345678"));
    True(DiscordBotService.ShouldSuppressPersonalRequest("mediaforge-request", "dm", "channel:123456789012345678"));
    False(DiscordBotService.ShouldSuppressPersonalRequest("mediaforge-request", "dm", "dm:223456789012345678"));
    False(DiscordBotService.ShouldSuppressPersonalRequest("achievement", "off", "channel:123456789012345678"));
    return Task.CompletedTask;
}

static Task TestLibraryHierarchyAsync()
{
    var seriesId = Guid.NewGuid();
    var seasonId = Guid.NewGuid();
    var episode = new Episode { SeriesId = seriesId, SeasonId = seasonId };
    var season = new Season { SeriesId = seriesId };
    True(LibraryNotificationService.HasNewParentInBatch(episode, new HashSet<Guid> { seriesId }, new HashSet<Guid>()));
    True(LibraryNotificationService.HasNewParentInBatch(episode, new HashSet<Guid>(), new HashSet<Guid> { seasonId }));
    True(LibraryNotificationService.HasNewParentInBatch(season, new HashSet<Guid> { seriesId }, new HashSet<Guid>()));
    False(LibraryNotificationService.HasNewParentInBatch(episode, new HashSet<Guid>(), new HashSet<Guid>()));

    var first = new Episode { ParentId = seasonId, Name = "Episode", ParentIndexNumber = 1, IndexNumber = 1 };
    var second = new Episode { ParentId = seasonId, Name = "Episode", ParentIndexNumber = 1, IndexNumber = 2 };
    False(string.Equals(
        LibraryNotificationService.BuildStableIdentity(first),
        LibraryNotificationService.BuildStableIdentity(second),
        StringComparison.Ordinal));
    return Task.CompletedTask;
}

static Task TestStickyTriggerAsync()
{
    False(DiscordBotService.ShouldIgnoreOwnStickyMessage(false, false, false));
    False(DiscordBotService.ShouldIgnoreOwnStickyMessage(false, true, true));
    False(DiscordBotService.ShouldIgnoreOwnStickyMessage(true, false, false));
    True(DiscordBotService.ShouldIgnoreOwnStickyMessage(true, true, false));
    True(DiscordBotService.ShouldIgnoreOwnStickyMessage(true, false, true));
    return Task.CompletedTask;
}

static string TempDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "jellix-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
static void EqualApprox(double expected, double actual, double tolerance) { if (Math.Abs(expected - actual) > tolerance) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
static void NotEqual<T>(T left, T right) { if (EqualityComparer<T>.Default.Equals(left, right)) throw new InvalidOperationException("Values should differ."); }
