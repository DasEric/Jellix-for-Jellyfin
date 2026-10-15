using System.Text;
using System.Text.Json;
using Jellix.Tests;
using Jellyfin.Plugin.Jellix.Configuration;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Discord;
using Jellyfin.Plugin.Jellix.Helpers;
using Jellyfin.Plugin.Jellix.Integrations;
using Jellyfin.Plugin.Jellix.Models;
using Jellyfin.Plugin.Jellix.Security;
using Jellyfin.Plugin.Jellix.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
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
    ("broken optional H0melab bridge stays isolated", TestH0melabIsolationAsync),
    ("H0melab bridge preserves protocol user and connector decisions", TestH0melabContractAsync),
    ("legacy connector configuration migrates without conflicting JSON aliases", TestH0melabConfigurationAsync),
    ("managed footers are readable and recover exact legacy or pending snapshots", TestManagedFootersAsync),
    ("schema six retains message IDs and upgrades unchanged snapshots in place", TestManagedUpgradeAsync),
    ("request results always resolve to requester DM", TestRequestDestinationAsync),
    ("library counts real episodes and versions", TestLibraryCountingAsync),
    ("series ingestion groups five seasons and late arrivals", TestSeriesPipelineAsync),
    ("initial baseline handles remote items without replay", TestInitialLibraryBaselineAsync),
    ("episode positions prevent duplicate split and combined imports", TestEpisodePositionImportsAsync),
    ("upgrade recovers children of a pending legacy series", TestPendingParentUpgradeAsync),
    ("series batches persist across restart and split after inactivity", TestSeriesPersistenceAsync),
    ("managed delivery recovers uncertain sends and edits in place", TestManagedDeliveryAsync),
    ("outbox does not acknowledge a newer pending revision", TestManagedRevisionAsync),
    ("legacy library jobs migrate before delivery", TestLibraryMigrationAsync),
    ("library recovery includes more than 1000 pending items", TestLibraryRecoveryAsync),
    ("overview formats actual storage and episode totals", TestOverviewAsync),
    ("storage resolves longest mount and deduplicates bind identity", TestStorageMountsAsync),
    ("storage probes a real filesystem and handles missing paths", TestStorageProbeAsync),
    ("library selection excludes other libraries and remote items", TestLibrarySelectionAsync),
    ("managed messages honor channel and feature settings", TestManagedSettingsAsync),
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
        Console.Error.WriteLine("FAIL " + test.Name + ": " + exception);
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
        verify.CommandText = "SELECT COUNT(*) FROM NotificationOutbox WHERE Kind='mediaforge-request' AND CompletedUtc IS NULL;";
        Equal(0L, Convert.ToInt64(await verify.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture));
        verify.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Version=7;";
        Equal(1L, Convert.ToInt64(await verify.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture));
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(path, true);
    }
}

static Task TestH0melabIsolationAsync()
{
    var bridge = new H0melabBridgeClient(new Jellyfin.Plugin.AniWorld.Integration.ThrowingServiceProvider());
    False(bridge.IsAvailable);
    return Task.CompletedTask;
}

static async Task TestH0melabContractAsync()
{
    var connector = new Jellyfin.Plugin.AniWorld.Integration.JellixBridge();
    var bridge = new H0melabBridgeClient(new Jellyfin.Plugin.AniWorld.Integration.BridgeServiceProvider(connector));
    True(bridge.IsAvailable);
    var user = Guid.NewGuid();
    foreach (var status in new[] { "pending", "queued", "downloading", "failed" })
    {
        connector.Result = JsonSerializer.Serialize(new { id = 17, title = "Example", status });
        using var response = await bridge.InvokeAsync("submit", user, "User", new { selectionToken = "opaque" }, CancellationToken.None).ConfigureAwait(false);
        Equal(status, response.RootElement.GetProperty("status").GetString());
        Equal(("1", "submit", user.ToString("N"), "{\"selectionToken\":\"opaque\"}"), connector.LastCall);
    }
    True(DiscordBotService.RequestStatusLabel("pending").Contains("Freigabe", StringComparison.Ordinal));
    True(DiscordBotService.RequestStatusLabel("queued").Contains("Warteschlange", StringComparison.Ordinal));
    connector.Failure = new InvalidOperationException("secret API key and upstream body");
    try
    {
        using var unexpected = await bridge.InvokeAsync("list", user, "User", null, CancellationToken.None).ConfigureAwait(false);
        throw new InvalidOperationException("Expected bridge rejection.");
    }
    catch (H0melabBridgeException exception) { False(exception.ToString().Contains("secret API", StringComparison.Ordinal)); }
}

static Task TestH0melabConfigurationAsync()
{
    var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
    using var input = new StringReader("<PluginConfiguration><MediaForgeEnabled>true</MediaForgeEnabled><MediaForgePollSeconds>90</MediaForgePollSeconds></PluginConfiguration>");
    using var reader = System.Xml.XmlReader.Create(input, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
    var config = (PluginConfiguration)serializer.Deserialize(reader)!;
    True(config.H0melabEnabled); Equal(90, config.H0melabPollSeconds);
    var json = JsonSerializer.Serialize(config);
    False(json.Contains("MediaForge", StringComparison.Ordinal));
    False(Newtonsoft.Json.JsonConvert.SerializeObject(config).Contains("MediaForge", StringComparison.Ordinal));
    using var output = new StringWriter();
    serializer.Serialize(output, config);
    using var roundTrip = new StringReader(output.ToString());
    using var roundTripReader = System.Xml.XmlReader.Create(roundTrip, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
    True(((PluginConfiguration)serializer.Deserialize(roundTripReader)!).H0melabEnabled);
    return Task.CompletedTask;
}

static Task TestManagedFootersAsync()
{
    const string payload = "{\"title\":\"Library\",\"description\":\"66 movies\"}";
    var footer = DiscordBotService.LibraryFooter("overview:123:456");
    False(footer.Contains("123", StringComparison.Ordinal));
    False(footer.Contains("overview:", StringComparison.Ordinal));
    True(DiscordBotService.IsManagedLibraryFooter(footer));
    var embed = JellixEmbeds.Create("Library", "66 movies", footer: footer).Build();
    True(DiscordBotService.MatchesManagedSnapshot(embed, "overview:123:456", payload));
    False(DiscordBotService.MatchesManagedSnapshot(embed, "overview:123:456", null));
    False(DiscordBotService.MatchesManagedSnapshot(embed, "series:test", payload));
    False(DiscordBotService.MatchesManagedSnapshot(embed, "overview:123:456", "{\"title\":\"Library\",\"description\":\"67 movies\"}"));
    var legacy = JellixEmbeds.Create("Old", footer: "Jellix · overview:123:456").Build();
    True(DiscordBotService.MatchesManagedSnapshot(legacy, "overview:123:456", null));
    False(DiscordBotService.MatchesManagedSnapshot(legacy, "overview:123:789", null));
    var repaired = DiscordBotService.RepairLibraryFooter(legacy, "overview:123:456");
    Equal(legacy.Title, repaired.Title); Equal(legacy.Timestamp, repaired.Timestamp);
    Equal(footer, repaired.Footer?.Text);
    Equal(legacy.Footer?.Text, DiscordBotService.RepairLibraryFooter(legacy, "overview:other").Footer?.Text);
    return Task.CompletedTask;
}

static async Task TestManagedUpgradeAsync()
{
    var path = TempDirectory();
    const string payload = "legacy snapshot";
    try
    {
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(path, "jellix.db")}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ManagedMessages(MessageKey TEXT PRIMARY KEY, ChannelId TEXT NOT NULL, MessageId TEXT NULL, ContentHash TEXT NOT NULL DEFAULT '', AttemptedUtc TEXT NOT NULL);
                INSERT INTO ManagedMessages VALUES('overview:123:456', '456', '42', $hash, $utc);
                """;
            command.Parameters.AddWithValue("$hash", LibraryMedia.Hash(payload));
            command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        using var database = new JellixDatabase(path);
        var before = (await database.ListManagedMessagesAsync(CancellationToken.None).ConfigureAwait(false)).Single();
        Equal("42", before.MessageId); Equal<string?>(null, before.AttemptPayload);
        var transport = new MemoryManagedTransport();
        transport.Messages["42"] = (before.Key, payload);
        await new ManagedMessagePublisher(database).PublishAsync(before.Key, "456", payload, transport, CancellationToken.None).ConfigureAwait(false);
        Equal(0, transport.Creates); Equal(1, transport.Updates);
        var after = (await database.GetManagedMessageAsync(before.Key, CancellationToken.None).ConfigureAwait(false))!;
        Equal("42", after.MessageId); NotEqual(before.ContentHash, after.ContentHash);
        await new ManagedMessagePublisher(database).PublishAsync(before.Key, "456", payload, transport, CancellationToken.None).ConfigureAwait(false);
        Equal(1, transport.Updates);
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
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
    Equal<string?>(null, H0melabMonitoringService.ResolveRequestDestination(link, "off"));
    Equal("dm:223456789012345678", H0melabMonitoringService.ResolveRequestDestination(link, "dm"));
    Equal("dm:223456789012345678", H0melabMonitoringService.ResolveRequestDestination(link, "channel"));
    True(DiscordBotService.ShouldSuppressPersonalRequest("h0melab-request", "off", "dm:223456789012345678"));
    True(DiscordBotService.ShouldSuppressPersonalRequest("h0melab-request", "dm", "channel:123456789012345678"));
    False(DiscordBotService.ShouldSuppressPersonalRequest("h0melab-request", "dm", "dm:223456789012345678"));
    True(DiscordBotService.ShouldSuppressPersonalRequest("mediaforge-request", "off", "dm:223456789012345678"));
    True(DiscordBotService.ShouldSuppressPersonalRequest("mediaforge-request", "dm", "channel:123456789012345678"));
    False(DiscordBotService.ShouldSuppressPersonalRequest("mediaforge-request", "dm", "dm:223456789012345678"));
    False(DiscordBotService.ShouldSuppressPersonalRequest("achievement", "off", "channel:123456789012345678"));
    return Task.CompletedTask;
}

static Task TestLibraryCountingAsync()
{
    var seriesId = Guid.NewGuid();
    var root = Path.GetTempPath();
    var one = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId, Path = Path.Combine(root, "e1.mkv"), ParentIndexNumber = 1, IndexNumber = 1, IndexNumberEnd = 2 };
    var duplicate = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId, Path = Path.Combine(root, "e2.mkv"), ParentIndexNumber = 1, IndexNumber = 2 };
    var special = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId, Path = Path.Combine(root, "special.mkv"), ParentIndexNumber = 0, IndexNumber = 1 };
    var virtualEpisode = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId, Path = Path.Combine(root, "missing.mkv"), ParentIndexNumber = 2, IndexNumber = 1, IsVirtualItem = true };
    var movie = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(root, "movie.mkv") };
    movie.ProviderIds["Tmdb"] = "123";
    var alternate = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(root, "movie-4k.mkv") };
    alternate.ProviderIds["Tmdb"] = "123";
    var counts = LibraryMedia.Count([one, duplicate, special, virtualEpisode, movie, alternate], _ => "series");
    Equal(new LibraryCounts(1, 1, 3), counts);
    Equal((3L, 1, 1), LibraryMedia.CountEpisodes([one, duplicate, special, virtualEpisode]));
    NotEqual(LibraryMedia.EpisodeIdentity(one, "series"), LibraryMedia.EpisodeIdentity(duplicate, "series"));
    var originalIdentity = LibraryMedia.Identity(movie);
    movie.ProviderIds["Other"] = "later-metadata";
    Equal(originalIdentity, LibraryMedia.Identity(movie));
    var malformed = new Episode { Id = Guid.NewGuid(), Path = Path.Combine(root, "malformed.mkv"), ParentIndexNumber = 1, IndexNumber = int.MaxValue, IndexNumberEnd = int.MaxValue };
    Equal(1L, LibraryMedia.CountEpisodes([malformed]).Episodes);
    True(LibraryMedia.DescribeSeries("Specials only", [special], true).Contains("(Specials)", StringComparison.Ordinal));
    return Task.CompletedTask;
}

static async Task TestSeriesPipelineAsync()
{
    var path = TempDirectory();
    try
    {
        using var database = new JellixDatabase(path);
        var config = new PluginConfiguration { BotEnabled = true, NewMediaNotificationsEnabled = true, NewMediaChannelId = "123456789012345678", SeriesQuietSeconds = 120 };
        var (library, proxy) = LibraryProxy.Create();
        proxy.Folders.Add(new VirtualFolderInfo { ItemId = Guid.NewGuid().ToString("N"), Name = "Series", Locations = [path], CollectionType = CollectionTypeOptions.tvshows });
        using var catalog = new LibraryCatalog(library, () => config);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        using var service = new LibraryNotificationService(library, null!, database, catalog, null!, NullLogger<LibraryNotificationService>.Instance, () => config, clock);
        var series = new Series { Id = Guid.NewGuid(), Name = "Testserie", Path = Path.Combine(path, "Testserie") };
        proxy.Items[series.Id] = series;
        var keys = new Dictionary<Guid, string>();
        var transport = new MemoryManagedTransport();
        var publisher = new ManagedMessagePublisher(database);
        await service.ObserveAsync(series, keys, CancellationToken.None).ConfigureAwait(false);
        for (var season = 1; season <= 5; season++)
        {
            var parent = new Season { Id = Guid.NewGuid(), SeriesId = series.Id, IndexNumber = season, Path = Path.Combine(series.Path, "Season " + season) };
            proxy.Items[parent.Id] = parent;
            await service.ObserveAsync(parent, keys, CancellationToken.None).ConfigureAwait(false);
            for (var number = 1; number <= 12; number++)
            {
                var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeasonId = parent.Id, ParentIndexNumber = season, IndexNumber = number, Path = Path.Combine(parent.Path, number + ".mkv") };
                proxy.Items[episode.Id] = episode;
                await service.ObserveAsync(episode, keys, CancellationToken.None).ConfigureAwait(false);
                await service.ObserveAsync(episode, keys, CancellationToken.None).ConfigureAwait(false);
            }
            clock.Now = clock.Now.AddMinutes(10);
            await service.FlushSeriesAsync(CancellationToken.None).ConfigureAwait(false);
            if (season == 1)
            {
                var firstJob = (await database.GetNextNotificationAsync(CancellationToken.None).ConfigureAwait(false))!;
                using var firstPayload = JsonDocument.Parse(firstJob.PayloadJson);
                await publisher.PublishAsync(firstPayload.RootElement.GetProperty("managedKey").GetString()!, config.NewMediaChannelId, firstJob.PayloadJson, transport, CancellationToken.None).ConfigureAwait(false);
                await database.CompleteManagedNotificationAsync(firstJob, CancellationToken.None).ConfigureAwait(false);
            }
        }
        Equal(1L, await database.GetPendingNotificationCountAsync(CancellationToken.None).ConfigureAwait(false));
        var job = (await database.GetNextNotificationAsync(CancellationToken.None).ConfigureAwait(false))!;
        Equal("library-series", job.Kind);
        using var payload = JsonDocument.Parse(job.PayloadJson);
        True(payload.RootElement.GetProperty("description").GetString()!.Contains("5 Staffeln (60 Folgen)", StringComparison.Ordinal));
        await publisher.PublishAsync(payload.RootElement.GetProperty("managedKey").GetString()!, config.NewMediaChannelId, job.PayloadJson, transport, CancellationToken.None).ConfigureAwait(false);
        Equal(1, transport.Creates); Equal(1, transport.Updates);
        Equal(new LibraryCounts(0, 1, 60), await catalog.GetCountsAsync(true, CancellationToken.None).ConfigureAwait(false));
        await database.CompleteManagedNotificationAsync(job, CancellationToken.None).ConfigureAwait(false);
        foreach (var episode in proxy.Items.Values.OfType<Episode>()) await service.ObserveAsync(episode, keys, CancellationToken.None).ConfigureAwait(false);
        await service.FlushSeriesAsync(CancellationToken.None).ConfigureAwait(false);
        Equal(0L, await database.GetPendingNotificationCountAsync(CancellationToken.None).ConfigureAwait(false));
        // Episode-only mode does not require a new parent event.
        config.NewMediaNotificationsEnabled = false;
        config.NewEpisodeNotificationsEnabled = true;
        var next = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = 5, IndexNumber = 13, Path = Path.Combine(series.Path, "late.mkv") };
        proxy.Items[next.Id] = next;
        await service.ObserveAsync(next, keys, CancellationToken.None).ConfigureAwait(false);
        clock.Now = clock.Now.AddMinutes(3);
        await service.FlushSeriesAsync(CancellationToken.None).ConfigureAwait(false);
        var lateJob = (await database.GetNextNotificationAsync(CancellationToken.None).ConfigureAwait(false))!;
        Equal(job.Id, lateJob.Id);
        using var latePayload = JsonDocument.Parse(lateJob.PayloadJson);
        True(latePayload.RootElement.GetProperty("description").GetString()!.Contains("61 Folgen", StringComparison.Ordinal));
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
}

static async Task TestInitialLibraryBaselineAsync()
{
    var path = TempDirectory();
    try
    {
        using var database = new JellixDatabase(path);
        var config = new PluginConfiguration { BotEnabled = true, NewMediaNotificationsEnabled = true, NewEpisodeNotificationsEnabled = true, NewMediaChannelId = "123456789012345678" };
        var (library, proxy) = LibraryProxy.Create();
        proxy.Folders.Add(new VirtualFolderInfo { ItemId = Guid.NewGuid().ToString("N"), Locations = [path] });
        var remote = new Movie { Id = Guid.NewGuid(), Path = "https://example.invalid/remote.mkv" };
        var local = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(path, "old.mkv") };
        proxy.Items[remote.Id] = remote; proxy.Items[local.Id] = local;
        using var catalog = new LibraryCatalog(library, () => config);
        using var service = new LibraryNotificationService(library, null!, database, catalog, null!, NullLogger<LibraryNotificationService>.Instance, () => config, new TestTimeProvider(DateTimeOffset.UtcNow));
        await service.InitializeLibraryStateAsync(CancellationToken.None).ConfigureAwait(false);
        var keys = new Dictionary<Guid, string>();
        await service.ObserveAsync(local, keys, CancellationToken.None).ConfigureAwait(false);
        await service.ObserveAsync(remote, keys, CancellationToken.None).ConfigureAwait(false);
        Equal(0L, await database.GetPendingNotificationCountAsync(CancellationToken.None).ConfigureAwait(false));
        Equal(0, (await database.ListPendingLibraryAnnouncementsAsync(CancellationToken.None).ConfigureAwait(false)).Count);
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
}

static async Task TestPendingParentUpgradeAsync()
{
    var path = TempDirectory();
    var series = new Series { Id = Guid.NewGuid(), Name = "Legacy pending series", Path = Path.Combine(path, "series") };
    var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 1, Path = Path.Combine(series.Path, "e1.mkv") };
    var movie = new Movie { Id = Guid.NewGuid(), Name = "Legacy pending movie", Path = Path.Combine(path, "movie.mkv") };
    movie.ProviderIds["Tmdb"] = "42";
    try
    {
        using (var previous = new JellixDatabase(path))
        {
            await previous.SeedLibraryItemsAsync([series.Id, episode.Id, movie.Id], CancellationToken.None).ConfigureAwait(false);
            await previous.SeedLibraryIdentitiesAsync([(LibraryNotificationService.BuildStableIdentity(series), series.Id), (LibraryNotificationService.BuildStableIdentity(episode), episode.Id), (LibraryNotificationService.BuildStableIdentity(movie), movie.Id)], CancellationToken.None).ConfigureAwait(false);
            await previous.SetBotStateAsync("library-baseline-v1", DateTime.UtcNow.AddDays(-2).ToString("O"), CancellationToken.None).ConfigureAwait(false);
            await previous.SetBotStateAsync("library-baseline-announced-v2", "1", CancellationToken.None).ConfigureAwait(false);
            await previous.EnqueueNotificationAsync(NotificationPriority.Low, "library", "channel:123456789012345678", "{}", "library:" + LibraryNotificationService.BuildStableIdentity(series), DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
            await previous.EnqueueNotificationAsync(NotificationPriority.Low, "library", "channel:123456789012345678", "{}", "library:" + LibraryNotificationService.BuildStableIdentity(movie), DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(path, "jellix.db")}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM BotState WHERE Key='library-grouped-outbox-v1';";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        using var database = new JellixDatabase(path);
        var config = new PluginConfiguration { BotEnabled = true, NewMediaNotificationsEnabled = true, NewMediaChannelId = "123456789012345678" };
        var (library, proxy) = LibraryProxy.Create();
        proxy.Folders.Add(new VirtualFolderInfo { ItemId = Guid.NewGuid().ToString("N"), Locations = [path] });
        proxy.Items[series.Id] = series; proxy.Items[episode.Id] = episode; proxy.Items[movie.Id] = movie;
        using var catalog = new LibraryCatalog(library, () => config);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        using var service = new LibraryNotificationService(library, null!, database, catalog, null!, NullLogger<LibraryNotificationService>.Instance, () => config, clock);
        await service.InitializeLibraryStateAsync(CancellationToken.None).ConfigureAwait(false);
        True(await database.IsLibraryItemPendingAsync(episode.Id, CancellationToken.None).ConfigureAwait(false));
        var keys = new Dictionary<Guid, string>();
        await service.ObserveAsync(series, keys, CancellationToken.None).ConfigureAwait(false);
        await service.ObserveAsync(episode, keys, CancellationToken.None).ConfigureAwait(false);
        await service.ObserveAsync(movie, keys, CancellationToken.None).ConfigureAwait(false);
        clock.Now = clock.Now.AddMinutes(3);
        await service.FlushSeriesAsync(CancellationToken.None).ConfigureAwait(false);
        Equal(2L, await database.GetPendingNotificationCountAsync(CancellationToken.None).ConfigureAwait(false));
        var recoveredMovie = (await database.GetNextNotificationAsync(CancellationToken.None).ConfigureAwait(false))!;
        Equal("library", recoveredMovie.Kind);
        True(recoveredMovie.DedupeKey.StartsWith("library-movie:", StringComparison.Ordinal));
        await database.CompleteNotificationAsync(recoveredMovie.Id, CancellationToken.None).ConfigureAwait(false);
        Equal("library-series", (await database.GetNextNotificationAsync(CancellationToken.None).ConfigureAwait(false))!.Kind);
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
}

static async Task TestEpisodePositionImportsAsync()
{
    var path = TempDirectory();
    try
    {
        using var database = new JellixDatabase(path);
        var config = new PluginConfiguration { BotEnabled = true, NewEpisodeNotificationsEnabled = true, NewMediaChannelId = "123456789012345678" };
        var (library, proxy) = LibraryProxy.Create();
        proxy.Folders.Add(new VirtualFolderInfo { ItemId = Guid.NewGuid().ToString("N"), Locations = [path] });
        var series = new Series { Id = Guid.NewGuid(), Path = Path.Combine(path, "series"), Name = "Ranges" };
        var old = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, Path = Path.Combine(series.Path, "e1-e2.mkv"), ParentIndexNumber = 1, IndexNumber = 1, IndexNumberEnd = 2 };
        proxy.Items[series.Id] = series; proxy.Items[old.Id] = old;
        using var catalog = new LibraryCatalog(library, () => config);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        using var service = new LibraryNotificationService(library, null!, database, catalog, null!, NullLogger<LibraryNotificationService>.Instance, () => config, clock);
        await service.InitializeLibraryStateAsync(CancellationToken.None).ConfigureAwait(false);
        var keys = new Dictionary<Guid, string>();
        var split = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, Path = Path.Combine(series.Path, "e2.mkv"), ParentIndexNumber = 1, IndexNumber = 2 };
        proxy.Items[split.Id] = split;
        await service.ObserveAsync(split, keys, CancellationToken.None).ConfigureAwait(false);
        clock.Now = clock.Now.AddMinutes(3);
        await service.FlushSeriesAsync(CancellationToken.None).ConfigureAwait(false);
        Equal(0L, await database.GetPendingNotificationCountAsync(CancellationToken.None).ConfigureAwait(false));
        var partlyNew = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, Path = Path.Combine(series.Path, "e2-e3.mkv"), ParentIndexNumber = 1, IndexNumber = 2, IndexNumberEnd = 3 };
        proxy.Items[partlyNew.Id] = partlyNew;
        await service.ObserveAsync(partlyNew, keys, CancellationToken.None).ConfigureAwait(false);
        clock.Now = clock.Now.AddMinutes(3);
        await service.FlushSeriesAsync(CancellationToken.None).ConfigureAwait(false);
        var job = (await database.GetNextNotificationAsync(CancellationToken.None).ConfigureAwait(false))!;
        using var payload = JsonDocument.Parse(job.PayloadJson);
        True(payload.RootElement.GetProperty("description").GetString()!.Contains("1 Staffel (1 Folge)", StringComparison.Ordinal));
        Equal(3L, (await catalog.GetCountsAsync(true, CancellationToken.None).ConfigureAwait(false)).Episodes);
        // Recreated Jellyfin IDs must rebind the stored logical positions.
        proxy.Items.Remove(partlyNew.Id);
        var replacement = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, Path = partlyNew.Path, ParentIndexNumber = 1, IndexNumber = 2, IndexNumberEnd = 3 };
        proxy.Items[replacement.Id] = replacement;
        await service.ObserveAsync(replacement, keys, CancellationToken.None).ConfigureAwait(false);
        var batch = (await database.ListSeriesMembersAsync(payload.RootElement.GetProperty("managedKey").GetString()!["series:".Length..], CancellationToken.None).ConfigureAwait(false)).Single();
        Equal(replacement.Id, batch.ItemId);
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
}

static async Task TestSeriesPersistenceAsync()
{
    var path = TempDirectory();
    var now = DateTime.UtcNow.AddHours(-4);
    var series = Guid.NewGuid();
    string id;
    try
    {
        using (var database = new JellixDatabase(path))
        {
            var parent = Guid.NewGuid();
            await database.RegisterParentTriggerAsync(parent, now.AddDays(-10), CancellationToken.None).ConfigureAwait(false);
            True(await database.HasParentTriggerAsync(parent, Guid.Empty, now, CancellationToken.None).ConfigureAwait(false));
            await database.ConsumeParentTriggersAsync(parent, Guid.Empty, now, CancellationToken.None).ConfigureAwait(false);
            False(await database.HasParentTriggerAsync(parent, Guid.Empty, now.AddDays(2), CancellationToken.None).ConfigureAwait(false));
            await database.AddSeriesMemberAsync("series", series, "123", "episode:1", Guid.NewGuid(), now, TimeSpan.FromHours(24), CancellationToken.None).ConfigureAwait(false);
            await database.AddSeriesMemberAsync("series", series, "123", "episode:1", Guid.NewGuid(), now.AddHours(1), TimeSpan.FromHours(24), CancellationToken.None).ConfigureAwait(false);
            var batch = (await database.ListReadySeriesBatchesAsync(now.AddMinutes(3), CancellationToken.None).ConfigureAwait(false)).Single();
            Equal(1, batch.Revision); id = batch.Id;
            await database.QueueSeriesBatchAsync(batch, "first", CancellationToken.None).ConfigureAwait(false);
        }
        using (var database = new JellixDatabase(path))
        {
            await database.AddSeriesMemberAsync("series", series, "123", "episode:2", Guid.NewGuid(), now.AddHours(2), TimeSpan.FromHours(24), CancellationToken.None).ConfigureAwait(false);
            var batch = (await database.ListReadySeriesBatchesAsync(now.AddHours(3), CancellationToken.None).ConfigureAwait(false)).Single();
            Equal(id, batch.Id); Equal(2, batch.Revision);
            await database.QueueSeriesBatchAsync(batch, "second", CancellationToken.None).ConfigureAwait(false);
            Equal(1L, await database.GetPendingNotificationCountAsync(CancellationToken.None).ConfigureAwait(false));
            await database.AddSeriesMemberAsync("series", series, "123", "episode:3", Guid.NewGuid(), now.AddHours(27), TimeSpan.FromHours(24), CancellationToken.None).ConfigureAwait(false);
            var later = (await database.ListReadySeriesBatchesAsync(now.AddHours(28), CancellationToken.None).ConfigureAwait(false)).Single();
            NotEqual(id, later.Id);
            Equal(1, later.Revision);
        }
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
}

static async Task TestManagedDeliveryAsync()
{
    var path = TempDirectory();
    try
    {
        using var database = new JellixDatabase(path);
        var publisher = new ManagedMessagePublisher(database);
        var transport = new MemoryManagedTransport { FailAfterCreate = true };
        try { await publisher.PublishAsync("series:test", "123", "first", transport, CancellationToken.None).ConfigureAwait(false); }
        catch (IOException) { }
        Equal(1, transport.Creates);
        // A new publisher represents a process restart after an uncertain send.
        publisher = new ManagedMessagePublisher(database);
        Equal("first", (await database.GetManagedMessageAsync("series:test", CancellationToken.None).ConfigureAwait(false))!.AttemptPayload);
        await publisher.PublishAsync("series:test", "123", "latest", transport, CancellationToken.None).ConfigureAwait(false);
        Equal("first", transport.RecoveryPayload);
        Equal("latest", transport.Messages.Single().Value.Payload);
        Equal(1, transport.Creates); Equal(1, transport.Messages.Count);
        var updates = transport.Updates;
        await publisher.PublishAsync("series:test", "123", "latest", transport, CancellationToken.None).ConfigureAwait(false);
        Equal(updates, transport.Updates);
        await publisher.PublishAsync("series:test", "123", "second", transport, CancellationToken.None).ConfigureAwait(false);
        Equal(1, transport.Creates); Equal(updates + 1, transport.Updates);
        transport.Messages.Clear();
        await publisher.PublishAsync("series:test", "123", "second", transport, CancellationToken.None).ConfigureAwait(false);
        Equal(2, transport.Creates); Equal(1, transport.Messages.Count);
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
}

static async Task TestManagedRevisionAsync()
{
    var path = TempDirectory();
    try
    {
        using var database = new JellixDatabase(path);
        await database.QueueOverviewAsync("123", "old", "overview:test", CancellationToken.None).ConfigureAwait(false);
        var old = (await database.GetNextNotificationAsync(CancellationToken.None).ConfigureAwait(false))!;
        await database.QueueOverviewAsync("123", "new", "overview:test", CancellationToken.None).ConfigureAwait(false);
        await database.CompleteManagedNotificationAsync(old, CancellationToken.None).ConfigureAwait(false);
        Equal(1L, await database.GetPendingNotificationCountAsync(CancellationToken.None).ConfigureAwait(false));
        var current = (await database.GetNextNotificationAsync(CancellationToken.None).ConfigureAwait(false))!;
        Equal("new", current.PayloadJson);
        await database.CompleteManagedNotificationAsync(current, CancellationToken.None).ConfigureAwait(false);
        Equal(0L, await database.GetPendingNotificationCountAsync(CancellationToken.None).ConfigureAwait(false));
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
}

static async Task TestLibraryMigrationAsync()
{
    var path = TempDirectory();
    var id = Guid.NewGuid();
    try
    {
        using (var database = new JellixDatabase(path))
        {
            await database.SeedLibraryItemsAsync([id], CancellationToken.None).ConfigureAwait(false);
            await database.SeedLibraryIdentitiesAsync([("legacy", id)], CancellationToken.None).ConfigureAwait(false);
            await database.EnqueueNotificationAsync(NotificationPriority.Low, "library", "channel:123", "{}", "library:legacy", DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(path, "jellix.db")}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM BotState WHERE Key='library-grouped-outbox-v1';";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        using (var database = new JellixDatabase(path))
        {
            await database.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            True(await database.IsLibraryItemPendingAsync(id, CancellationToken.None).ConfigureAwait(false));
            True(await database.IsLibraryIdentityPendingAsync("legacy", CancellationToken.None).ConfigureAwait(false));
            Equal(0L, await database.GetPendingNotificationCountAsync(CancellationToken.None).ConfigureAwait(false));
        }
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
}

static async Task TestLibraryRecoveryAsync()
{
    var path = TempDirectory();
    try
    {
        using var database = new JellixDatabase(path);
        for (var index = 0; index < 1005; index++) await database.TryRegisterLibraryItemAsync(Guid.NewGuid(), DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
        Equal(1005, (await database.ListPendingLibraryAnnouncementsAsync(CancellationToken.None).ConfigureAwait(false)).Count);
    }
    finally { SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
}

static Task TestOverviewAsync()
{
    var text = LibraryOverviewService.Describe(new LibraryCounts(1240, 186, 12438), [new StorageUsage("Medienspeicher", 4_000_000_000_000, 3_000_000_000_000, null)], false);
    True(text.Contains("186 Serien (12.438 Folgen insgesamt)", StringComparison.Ordinal));
    True(text.Contains("1,00 TB von 4,00 TB belegt · 3,00 TB frei (25 %)", StringComparison.Ordinal));
    var failure = LibraryOverviewService.Describe(new LibraryCounts(0, 0, 0), [new StorageUsage("Storage", null, null, "Storage unavailable")], true);
    True(failure.Contains("Storage unavailable", StringComparison.Ordinal));
    False(failure.Contains("0.00 GB", StringComparison.Ordinal));
    return Task.CompletedTask;
}

static Task TestStorageMountsAsync()
{
    var root = Path.GetTempPath();
    var media = Path.Combine(root, "media");
    var mounts = new[] { new MediaStorageReader.Mount(root, "system", "ext4"), new MediaStorageReader.Mount(media, "media", "ext4") };
    Equal("media", MediaStorageReader.FindMount(Path.Combine(media, "series"), mounts)!.Identity);
    False(LibraryMedia.IsUnder(Path.Combine(root, "media-other"), media));
    var first = MediaStorageReader.ParseMount("100 20 8:1 /films /media/films rw - ext4 /dev/sda1 rw")!;
    var second = MediaStorageReader.ParseMount("101 20 8:1 /series /media/series rw - ext4 /dev/sda1 rw")!;
    Equal(first.Identity, second.Identity);
    Equal("/media/with space", MediaStorageReader.ParseMount("101 20 8:2 / /media/with\\040space rw - ext4 /dev/sdb1 rw")!.Path);
    return Task.CompletedTask;
}

static Task TestManagedSettingsAsync()
{
    var config = new PluginConfiguration { BotEnabled = true, LibraryOverviewEnabled = true, LibraryOverviewChannelId = "123", NewEpisodeNotificationsEnabled = true, NewMediaChannelId = "456" };
    True(DiscordBotService.ShouldDeliverManagedLibrary("library-overview", "channel:123", config));
    False(DiscordBotService.ShouldDeliverManagedLibrary("library-overview", "channel:456", config));
    True(DiscordBotService.ShouldDeliverManagedLibrary("library-series", "channel:456", config));
    config.BotEnabled = false;
    False(DiscordBotService.ShouldDeliverManagedLibrary("library-series", "channel:456", config));
    True(DiscordBotService.IsManagedLibraryFooter("Jellix · overview:123:456"));
    True(DiscordBotService.IsManagedLibraryFooter("Jellix · series:batch"));
    False(DiscordBotService.IsManagedLibraryFooter("Jellix for Jellyfin"));
    return Task.CompletedTask;
}

static Task TestStorageProbeAsync()
{
    var path = TempDirectory();
    try
    {
        var reader = new MediaStorageReader();
        var usage = reader.Read([path, Path.GetDirectoryName(path)!], false);
        Equal(1, usage.Count);
        var overlay = OperatingSystem.IsLinux() && File.Exists("/proc/self/mountinfo")
            && MediaStorageReader.FindMount(path, File.ReadLines("/proc/self/mountinfo").Select(MediaStorageReader.ParseMount).OfType<MediaStorageReader.Mount>())?.Format is "overlay" or "overlayfs";
        if (overlay) { Equal<long?>(null, usage[0].TotalBytes); True(usage[0].Error is not null); }
        else { True(usage[0].TotalBytes > 0); True(usage[0].FreeBytes >= 0); Equal<string?>(null, usage[0].Error); }
        var missing = reader.Read([Path.Combine(path, "not-mounted")], false).Single();
        Equal<long?>(null, missing.TotalBytes);
        True(missing.Error is not null);
    }
    finally { Directory.Delete(path, true); }
    return Task.CompletedTask;
}

static async Task TestLibrarySelectionAsync()
{
    var path = TempDirectory();
    try
    {
        var config = new PluginConfiguration();
        var (library, proxy) = LibraryProxy.Create();
        var selectedId = Guid.NewGuid().ToString("N");
        proxy.Folders.Add(new VirtualFolderInfo { ItemId = selectedId, Name = "Selected", Locations = [Path.Combine(path, "selected")], CollectionType = CollectionTypeOptions.movies });
        proxy.Folders.Add(new VirtualFolderInfo { ItemId = Guid.NewGuid().ToString("N"), Name = "Other", Locations = [Path.Combine(path, "other")], CollectionType = CollectionTypeOptions.movies });
        var movie = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(path, "selected", "movie.mkv") };
        var other = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(path, "other", "movie.mkv") };
        var remote = new Movie { Id = Guid.NewGuid(), Path = "https://example.invalid/movie" };
        proxy.Items[movie.Id] = movie; proxy.Items[other.Id] = other; proxy.Items[remote.Id] = remote;
        using var catalog = new LibraryCatalog(library, () => config);
        Equal(2L, (await catalog.GetCountsAsync(true, CancellationToken.None).ConfigureAwait(false)).Movies);
        config.LibraryIds = selectedId;
        Equal(1L, (await catalog.GetCountsAsync(false, CancellationToken.None).ConfigureAwait(false)).Movies);
        False(catalog.IsSelected(other)); False(catalog.IsSelected(remote));
        config.LibraryIds = Guid.NewGuid().ToString("N");
        Equal(0L, (await catalog.GetCountsAsync(true, CancellationToken.None).ConfigureAwait(false)).Movies);
    }
    finally { Directory.Delete(path, true); }
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
