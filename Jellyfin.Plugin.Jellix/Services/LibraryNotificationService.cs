using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Configuration;
using Jellyfin.Plugin.Jellix.Models;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellix.Services;

/// <summary>Durable per-series aggregation; episode events never send individual messages.</summary>
public sealed class LibraryNotificationService : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailure = LoggerMessage.Define(LogLevel.Warning, new EventId(3001, "LibraryNotificationFailed"), "Jellix could not prepare a library notification");
    private readonly ILibraryManager _libraryManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly JellixDatabase _database;
    private readonly LibraryCatalog _catalog;
    private readonly ITaskManager _tasks;
    private readonly ILogger<LibraryNotificationService> _logger;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly TimeProvider _time;
    private readonly Channel<Guid> _items = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private bool _ready;

    public LibraryNotificationService(ILibraryManager libraryManager, IImageProcessor imageProcessor, JellixDatabase database, LibraryCatalog catalog, ITaskManager tasks, ILogger<LibraryNotificationService> logger)
        : this(libraryManager, imageProcessor, database, catalog, tasks, logger, () => Plugin.Instance?.Configuration, TimeProvider.System) { }

    internal LibraryNotificationService(ILibraryManager libraryManager, IImageProcessor imageProcessor, JellixDatabase database, LibraryCatalog catalog, ITaskManager tasks, ILogger<LibraryNotificationService> logger, Func<PluginConfiguration?> configuration, TimeProvider time)
    {
        _libraryManager = libraryManager;
        _imageProcessor = imageProcessor;
        _database = database;
        _catalog = catalog;
        _tasks = tasks;
        _logger = logger;
        _configuration = configuration;
        _time = time;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        try
        {
            await InitializeLibraryStateAsync(cancellationToken).ConfigureAwait(false);
            _ready = true;
        }
        catch (Exception exception) { LogFailure(_logger, exception); }
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task InitializeLibraryStateAsync(CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var existing = GetAllItems();
        var baseline = await _database.GetBotStateAsync("library-baseline-v1", cancellationToken).ConfigureAwait(false);
        if (baseline is null)
        {
            await _database.SeedLibraryItemsAsync(existing.Select(item => item.Id), cancellationToken).ConfigureAwait(false);
            baseline = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            await _database.SetBotStateAsync("library-baseline-v1", baseline, cancellationToken).ConfigureAwait(false);
        }

        if (await _database.GetBotStateAsync("library-baseline-announced-v2", cancellationToken).ConfigureAwait(false) is null
            && DateTime.TryParse(baseline, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var baselineUtc))
        {
            await _database.MarkLegacyLibraryBaselineAnnouncedAsync(baselineUtc.ToUniversalTime(), cancellationToken).ConfigureAwait(false);
            await _database.SetBotStateAsync("library-baseline-announced-v2", "1", cancellationToken).ConfigureAwait(false);
        }

        var pending = (await _database.ListPendingLibraryAnnouncementsAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        // Legacy parent jobs had already marked their children as covered.
        // Recover those actual episodes before seeding the new identity baseline.
        var coveredChildren = existing.OfType<Episode>().Where(episode => pending.Contains(episode.SeriesId) || pending.Contains(episode.SeasonId)).Select(episode => episode.Id).ToArray();
        if (coveredChildren.Length > 0)
        {
            await _database.RestorePendingLibraryItemsAsync(coveredChildren, cancellationToken).ConfigureAwait(false);
            pending.UnionWith(coveredChildren);
        }
        var known = await _database.ListKnownLibraryIdsAsync(cancellationToken).ConfigureAwait(false);
        if (await _database.GetBotStateAsync("library-logical-baseline-v1", cancellationToken).ConfigureAwait(false) is null)
        {
            var identities = new List<(string Identity, Guid ItemId)>();
            var keys = new Dictionary<Guid, string>();
            foreach (var item in existing.Where(item => known.Contains(item.Id) && !pending.Contains(item.Id)))
            {
                identities.Add((BuildStableIdentity(item), item.Id));
                if (LibraryMedia.IsLocal(item))
                {
                    identities.Add((await IdentityAsync(item, keys, cancellationToken).ConfigureAwait(false), item.Id));
                    if (item is Episode episode && keys.TryGetValue(episode.SeriesId, out var seriesKey))
                        identities.AddRange(LibraryMedia.EpisodePositions(episode, seriesKey).Select(position => (position.Identity, item.Id)));
                }
            }
            await _database.SeedLibraryIdentitiesAsync(identities, cancellationToken).ConfigureAwait(false);
            await _database.SetBotStateAsync("library-logical-baseline-v1", "1", cancellationToken).ConfigureAwait(false);
        }

        foreach (var id in pending.Concat(existing.Where(item => !known.Contains(item.Id)).Select(item => item.Id))) _items.Writer.TryWrite(id);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _items.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<BaseItem> GetAllItems() => _libraryManager.GetItemList(new InternalItemsQuery
    {
        Recursive = true, IsVirtualItem = false,
        IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
    });

    private void OnItemChanged(object? sender, ItemChangeEventArgs args)
    {
        if (args.Item is Movie or Series or Season or Episode && !args.Item.IsVirtualItem) _items.Writer.TryWrite(args.Item.Id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_ready) return;
        var nextReconcile = DateTime.UtcNow.AddMinutes(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ids = new HashSet<Guid>();
                while (_items.Reader.TryRead(out var id)) ids.Add(id);
                if (DateTime.UtcNow >= nextReconcile)
                {
                    var known = await _database.ListKnownLibraryIdsAsync(stoppingToken).ConfigureAwait(false);
                    ids.UnionWith(GetAllItems().Where(item => !known.Contains(item.Id)).Select(item => item.Id));
                    ids.UnionWith(await _database.ListPendingLibraryAnnouncementsAsync(stoppingToken).ConfigureAwait(false));
                    nextReconcile = DateTime.UtcNow.AddMinutes(5);
                }

                var ordered = ids.Select(id => _libraryManager.GetItemById(id)).OfType<BaseItem>()
                    .OrderBy(item => item switch { Series => 0, Season => 1, Movie => 2, _ => 3 }).ToArray();
                var keys = new Dictionary<Guid, string>();
                foreach (var item in ordered)
                {
                    try { await ObserveAsync(item, keys, stoppingToken).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is not OperationCanceledException) { LogFailure(_logger, exception); }
                }

                if (!IsLibraryScanRunning()) await FlushSeriesAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                LogFailure(_logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private bool IsLibraryScanRunning() => _tasks.ScheduledTasks.Any(task =>
        task.State is TaskState.Running or TaskState.Cancelling
        && task.ScheduledTask.GetType().Name.Contains("RefreshMediaLibrary", StringComparison.OrdinalIgnoreCase));

    private async Task<string> IdentityAsync(BaseItem item, Dictionary<Guid, string> keys, CancellationToken cancellationToken)
    {
        if (item is not Episode episode || episode.SeriesId == Guid.Empty) return LibraryMedia.Identity(item);
        if (!keys.TryGetValue(episode.SeriesId, out var key))
        {
            key = await _database.ResolveSeriesKeyAsync(episode.SeriesId, _catalog.SeriesKey(episode.SeriesId), cancellationToken).ConfigureAwait(false);
            keys[episode.SeriesId] = key;
        }
        return LibraryMedia.EpisodeIdentity(episode, key);
    }

    internal async Task ObserveAsync(BaseItem item, Dictionary<Guid, string> keys, CancellationToken cancellationToken)
    {
        if (await _database.IsLibraryItemKnownAsync(item.Id, cancellationToken).ConfigureAwait(false)
            && !await _database.IsLibraryItemPendingAsync(item.Id, cancellationToken).ConfigureAwait(false)) return;
        await _database.TryRegisterLibraryItemAsync(item.Id, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(item.Path)) return;
        if (!LibraryMedia.IsLocal(item))
        {
            await _database.MarkLibraryItemAnnouncedAsync(item.Id, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Parent IDs can be filled by a later ItemUpdated event.
        if (item is Episode { SeriesId: var seriesId } && seriesId == Guid.Empty) return;
        var identity = await IdentityAsync(item, keys, cancellationToken).ConfigureAwait(false);
        var isNew = await _database.TryRegisterLibraryIdentityAsync(identity, item.Id, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (!isNew && !await _database.IsLibraryIdentityPendingAsync(identity, cancellationToken).ConfigureAwait(false))
        {
            if (item is Episode replacement && keys.TryGetValue(replacement.SeriesId, out var replacementKey))
                foreach (var position in LibraryMedia.EpisodePositions(replacement, replacementKey))
                    await _database.RebindSeriesMemberAsync(position.Identity, item.Id, cancellationToken).ConfigureAwait(false);
            await _database.MarkLibraryItemAnnouncedAsync(item.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        var config = _configuration();
        var enabled = config?.BotEnabled == true && ulong.TryParse(config.NewMediaChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        if (enabled && _catalog.IsSelected(item))
        {
            if (item is Series or Season && config!.NewMediaNotificationsEnabled)
                await _database.RegisterParentTriggerAsync(item.Id, _time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
            else if (item is Movie && config!.NewMediaNotificationsEnabled && LibraryMedia.IsLocal(item))
                await QueueMovieAsync(item, identity, cancellationToken).ConfigureAwait(false);
        }

        if (item is Episode localEpisode)
        {
            var selected = enabled && _catalog.IsSelected(localEpisode);
            var cutoff = _time.GetUtcNow().UtcDateTime.AddHours(-Math.Clamp(config?.SeriesMergeHours ?? 24, 1, 168));
            var triggered = selected && config!.NewMediaNotificationsEnabled && (
                await _database.HasParentTriggerAsync(localEpisode.SeriesId, localEpisode.SeasonId, cutoff, cancellationToken).ConfigureAwait(false)
                || await _database.HasActiveSeriesBatchAsync(keys[localEpisode.SeriesId], config.NewMediaChannelId, cutoff, cancellationToken).ConfigureAwait(false));
            var announce = selected && (config!.NewEpisodeNotificationsEnabled || triggered);
            if (!announce && selected && config!.NewMediaNotificationsEnabled && (
                await _database.IsLibraryItemPendingAsync(localEpisode.SeriesId, cancellationToken).ConfigureAwait(false)
                || await _database.IsLibraryItemPendingAsync(localEpisode.SeasonId, cancellationToken).ConfigureAwait(false))) return;

            foreach (var position in LibraryMedia.EpisodePositions(localEpisode, keys[localEpisode.SeriesId]))
            {
                var newPosition = await _database.TryRegisterLibraryIdentityAsync(position.Identity, item.Id, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
                if (!newPosition && !await _database.IsLibraryIdentityPendingAsync(position.Identity, cancellationToken).ConfigureAwait(false))
                {
                    await _database.RebindSeriesMemberAsync(position.Identity, item.Id, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (announce)
                {
                    await _database.AddSeriesMemberAsync(keys[localEpisode.SeriesId], localEpisode.SeriesId, config!.NewMediaChannelId, position.Identity, item.Id,
                        _time.GetUtcNow().UtcDateTime, TimeSpan.FromHours(Math.Clamp(config.SeriesMergeHours, 1, 168)), cancellationToken, position.Number).ConfigureAwait(false);
                    await _database.ConsumeParentTriggersAsync(localEpisode.SeriesId, localEpisode.SeasonId, _time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
                }
                await _database.MarkLibraryIdentityAnnouncedAsync(position.Identity, cancellationToken).ConfigureAwait(false);
            }
        }

        // AnnouncedUtc is legacy ingestion state, not a delivery receipt.
        // Delivery is recorded separately by the outbox and ManagedMessages.
        await _database.MarkLibraryIdentityAnnouncedAsync(identity, cancellationToken).ConfigureAwait(false);
        await _database.MarkLibraryItemAnnouncedAsync(item.Id, cancellationToken).ConfigureAwait(false);
    }

    internal async Task FlushSeriesAsync(CancellationToken cancellationToken)
    {
        var config = _configuration();
        if (config is null) return;
        var batches = await _database.ListReadySeriesBatchesAsync(_time.GetUtcNow().UtcDateTime.AddSeconds(-Math.Clamp(config.SeriesQuietSeconds, 10, 1800)), cancellationToken).ConfigureAwait(false);
        foreach (var batch in batches)
        {
            var members = await _database.ListSeriesMembersAsync(batch.Id, cancellationToken).ConfigureAwait(false);
            var available = members.Select(member => (Member: member, Episode: _libraryManager.GetItemById(member.ItemId) as Episode))
                .Where(value => value.Episode is { } episode && LibraryMedia.IsLocal(episode) && _catalog.IsSelected(episode)
                    && (!value.Member.EpisodeNumber.HasValue || (value.Member.EpisodeNumber >= episode.IndexNumber
                        && value.Member.EpisodeNumber <= (episode.IndexNumberEnd ?? episode.IndexNumber)))).ToArray();
            if (available.Length == 0 || !config.BotEnabled || (!config.NewMediaNotificationsEnabled && !config.NewEpisodeNotificationsEnabled)
                || batch.ChannelId != config.NewMediaChannelId)
            {
                await _database.AcknowledgeSeriesBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var series = _libraryManager.GetItemById(batch.SeriesId);
            var episodes = available.Select(value => value.Episode!).ToArray();
            var structure = LibraryMedia.CountEpisodes(episodes);
            var counts = ((long)available.Length, structure.Seasons, structure.Specials);
            var name = series?.Name ?? episodes[0].SeriesName ?? episodes[0].Name;
            var imagePath = series is null ? null : await PreparePosterAsync(series).ConfigureAwait(false);
            var payload = JsonSerializer.Serialize(new
            {
                managedKey = "series:" + batch.Id,
                title = config.Language == "en" ? "📺 New on Jellyfin" : "📺 Neu auf Jellyfin",
                description = LibraryMedia.DescribeSeries(name, counts, config.Language == "en"),
                color = 0x00A4DCu, attachmentPath = imagePath,
            });
            await _database.QueueSeriesBatchAsync(batch, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task QueueMovieAsync(BaseItem item, string identity, CancellationToken cancellationToken)
    {
        var config = _configuration()!;
        var lines = new List<string> { "**" + LibraryMedia.Escape(item.Name.Length > 250 ? item.Name[..250] : item.Name) + "**" };
        if (item.ProductionYear.HasValue) lines.Add("📅 " + item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture));
        if (item.CommunityRating.HasValue) lines.Add("⭐ " + item.CommunityRating.Value.ToString("0.0", CultureInfo.InvariantCulture) + "/10");
        if (item.RunTimeTicks is > 0) lines.Add("⏱️ " + TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min");
        if (!string.IsNullOrWhiteSpace(item.Overview)) lines.Add(LibraryMedia.Escape(item.Overview.Length > 500 ? item.Overview[..500] + "…" : item.Overview));
        var payload = JsonSerializer.Serialize(new { title = config.Language == "en" ? "🆕 New on Jellyfin" : "🆕 Neu auf Jellyfin", description = string.Join('\n', lines), color = 0x00A4DCu, attachmentPath = await PreparePosterAsync(item).ConfigureAwait(false) });
        await _database.EnqueueNotificationAsync(NotificationPriority.Low, "library", "channel:" + config.NewMediaChannelId, payload, "library-movie:" + identity, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> PreparePosterAsync(BaseItem item)
    {
        try
        {
            if (!item.HasImage(ImageType.Primary)) return null;
            var image = await _imageProcessor.ProcessImage(new ImageProcessingOptions
            {
                Item = item, ItemId = item.Id, Image = item.GetImageInfo(ImageType.Primary, 0), ImageIndex = 0,
                MaxWidth = 600, MaxHeight = 900, Quality = 85, SupportedOutputFormats = _imageProcessor.GetSupportedImageOutputFormats(),
            }).ConfigureAwait(false);
            return File.Exists(image.Item1) ? image.Item1 : null;
        }
        catch (Exception exception) { LogFailure(_logger, exception); return null; }
    }

    // Recognizes pre-0.4 persisted identities during migration.
    internal static string BuildStableIdentity(BaseItem item)
    {
        var kind = item switch { Movie => "movie", Series => "series", Season => "season", Episode => "episode", _ => item.GetType().Name.ToLowerInvariant() };
        var position = item switch
        {
            Season season => $"|season:{season.IndexNumber.GetValueOrDefault()}",
            Episode episode => $"|season:{episode.ParentIndexNumber.GetValueOrDefault()}|episode:{episode.IndexNumber.GetValueOrDefault()}",
            _ => string.Empty,
        };
        var source = item.ProviderIds.Count > 0
            ? kind + "|" + string.Join('|', item.ProviderIds.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key.ToLowerInvariant() + ":" + pair.Value.Trim().ToLowerInvariant())) + position
            : !string.IsNullOrWhiteSpace(item.Path) ? kind + "|" + item.Path.Trim().Replace('\\', '/') + position
            : $"{item.GetType().Name}|{item.ParentId:N}|{item.Name}|{item.ProductionYear.GetValueOrDefault()}{position}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }
}
