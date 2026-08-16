using System.Text;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Helpers;
using Jellyfin.Plugin.Jellix.Integrations;
using Jellyfin.Plugin.Jellix.Models;
using Jellyfin.Plugin.Jellix.Security;
using Jellyfin.Plugin.Jellix.Services;
using Microsoft.Data.Sqlite;

SQLitePCL.Batteries_V2.Init();
var tests = new (string Name, Func<Task> Run)[]
{
    ("link code hashing is normalized", TestLinkHashAsync),
    ("secret storage is encrypted", TestSecretStoreAsync),
    ("web injection is idempotent", TestWebInjectionAsync),
    ("public password URLs reject unsafe components", TestPublicUrlAsync),
    ("database preserves one-to-one links and state", TestDatabaseAsync),
    ("database migrates legacy playback data", TestDatabaseMigrationAsync),
    ("playback accounting ignores pauses and seeks", TestPlaybackAsync),
    ("broken optional MediaForge bridge stays isolated", TestMediaForgeIsolationAsync),
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
        var baselineId = Guid.NewGuid();
        await database.SeedLibraryItemsAsync([baselineId], CancellationToken.None).ConfigureAwait(false);
        False(await database.IsLibraryItemPendingAsync(baselineId, CancellationToken.None).ConfigureAwait(false));
        var pendingId = Guid.NewGuid();
        True(await database.TryRegisterLibraryItemAsync(pendingId, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false));
        True(await database.IsLibraryItemPendingAsync(pendingId, CancellationToken.None).ConfigureAwait(false));
        await database.MarkLibraryItemAnnouncedAsync(pendingId, CancellationToken.None).ConfigureAwait(false);
        False(await database.IsLibraryItemPendingAsync(pendingId, CancellationToken.None).ConfigureAwait(false));
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
                """;
            command.Parameters.AddWithValue("$user", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$item", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
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
