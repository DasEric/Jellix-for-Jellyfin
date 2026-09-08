using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Models;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellix.Services;

/// <summary>Publishes genuinely new library items after a persistent initial baseline.</summary>
public sealed class LibraryNotificationService : BackgroundService
{
    private static readonly TimeSpan ParentAnnouncementWindow = TimeSpan.FromHours(24);
    private static readonly Action<ILogger, Exception?> LogFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(3001, "LibraryNotificationFailed"),
        "Jellix could not prepare a library notification");
    private readonly ILibraryManager _libraryManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly JellixDatabase _database;
    private readonly ILogger<LibraryNotificationService> _logger;
    private readonly Channel<Guid> _items = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private bool _ready;

    public LibraryNotificationService(
        ILibraryManager libraryManager,
        IImageProcessor imageProcessor,
        JellixDatabase database,
        ILogger<LibraryNotificationService> logger)
    {
        _libraryManager = libraryManager;
        _imageProcessor = imageProcessor;
        _database = database;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var baseline = await _database.GetBotStateAsync("library-baseline-v1", cancellationToken).ConfigureAwait(false);
            if (baseline is null)
            {
                var existing = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IsVirtualItem = false,
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
                });
                await _database.SeedLibraryItemsAsync(existing.Select(static value => value.Id), cancellationToken).ConfigureAwait(false);
                await _database.SetBotStateAsync("library-baseline-v1", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                baseline = await _database.GetBotStateAsync("library-baseline-v1", cancellationToken).ConfigureAwait(false);
            }

            if (await _database.GetBotStateAsync("library-baseline-announced-v2", cancellationToken).ConfigureAwait(false) is null
                && DateTime.TryParse(baseline, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var baselineUtc))
            {
                await _database.MarkLegacyLibraryBaselineAnnouncedAsync(baselineUtc.ToUniversalTime(), cancellationToken).ConfigureAwait(false);
                await _database.SetBotStateAsync("library-baseline-announced-v2", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            }

            var pendingItems = await _database.ListPendingLibraryAnnouncementsAsync(cancellationToken).ConfigureAwait(false);
            if (await _database.GetBotStateAsync("library-identity-baseline-v1", cancellationToken).ConfigureAwait(false) is null)
            {
                var pendingSet = pendingItems.ToHashSet();
                var existing = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IsVirtualItem = false,
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
                });
                await _database.SeedLibraryIdentitiesAsync(
                    existing.Where(item => !pendingSet.Contains(item.Id)).Select(item => (BuildStableIdentity(item), item.Id)),
                    cancellationToken).ConfigureAwait(false);
                await _database.SetBotStateAsync("library-identity-baseline-v1", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            }

            foreach (var pending in pendingItems)
            {
                _items.Writer.TryWrite(pending);
            }

            _ready = true;
        }
        catch (Exception exception)
        {
            LogFailure(_logger, exception);
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _libraryManager.ItemAdded += OnItemAdded;
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _items.Writer.TryComplete();
        try
        {
            if (ExecuteTask is not null)
            {
                await ExecuteTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_ready) return;
        await ProcessQueueAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        while (await _items.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var batch = new HashSet<Guid>();
                while (_items.Reader.TryRead(out var itemId)) batch.Add(itemId);
                if (batch.Count == 0)
                {
                    continue;
                }

                // Jellyfin emits parents and children independently during a
                // scan. Give the hierarchy a short settling window, then make
                // one decision for the complete group.
                await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken).ConfigureAwait(false);
                while (_items.Reader.TryRead(out var additionalId)) batch.Add(additionalId);
                await ProcessBatchAsync(batch, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogFailure(_logger, exception);
            }
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs eventArgs)
    {
        if (eventArgs.Item is Movie or Series or Season or Episode && !eventArgs.Item.IsVirtualItem)
        {
            _items.Writer.TryWrite(eventArgs.Item.Id);
        }
    }

    private async Task ProcessBatchAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        var candidates = new List<AnnouncementCandidate>();
        foreach (var itemId in itemIds)
        {
            var candidate = await PrepareCandidateAsync(itemId, cancellationToken).ConfigureAwait(false);
            if (candidate is not null) candidates.Add(candidate);
        }

        var config = Plugin.Instance?.Configuration;
        var parentAnnouncementsEnabled = config is { NewMediaNotificationsEnabled: true }
            && ulong.TryParse(config.NewMediaChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        var newSeries = parentAnnouncementsEnabled
            ? candidates.Where(candidate => candidate.Item is Series).Select(candidate => candidate.Item.Id).ToHashSet()
            : [];
        var newSeasons = parentAnnouncementsEnabled
            ? candidates.Where(candidate => candidate.Item is Season).Select(candidate => candidate.Item.Id).ToHashSet()
            : [];
        foreach (var candidate in candidates.OrderBy(static candidate => NotificationOrder(candidate.Item)))
        {
            if (await IsCoveredByParentAsync(candidate.Item, newSeries, newSeasons, parentAnnouncementsEnabled, cancellationToken).ConfigureAwait(false))
            {
                await MarkAnnouncedAsync(candidate, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await QueueAnnouncementAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AnnouncementCandidate?> PrepareCandidateAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (await _database.IsLibraryItemKnownAsync(itemId, cancellationToken).ConfigureAwait(false)
            && !await _database.IsLibraryItemPendingAsync(itemId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || item.IsVirtualItem)
        {
            if (await _database.IsLibraryItemPendingAsync(itemId, cancellationToken).ConfigureAwait(false))
            {
                await _database.MarkLibraryItemAnnouncedAsync(itemId, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        var newlyRegistered = await _database.TryRegisterLibraryItemAsync(itemId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (!newlyRegistered && !await _database.IsLibraryItemPendingAsync(itemId, cancellationToken).ConfigureAwait(false)) return null;

        var identity = BuildStableIdentity(item);
        var newIdentity = await _database.TryRegisterLibraryIdentityAsync(identity, itemId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (!newIdentity && !await _database.IsLibraryIdentityPendingAsync(identity, cancellationToken).ConfigureAwait(false))
        {
            await _database.MarkLibraryItemAnnouncedAsync(itemId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return new AnnouncementCandidate(item, identity);
    }

    private async Task<bool> IsCoveredByParentAsync(
        BaseItem item,
        HashSet<Guid> newSeries,
        HashSet<Guid> newSeasons,
        bool parentAnnouncementsEnabled,
        CancellationToken cancellationToken)
    {
        if (HasNewParentInBatch(item, newSeries, newSeasons)) return true;
        if (!parentAnnouncementsEnabled) return false;
        if (item is Season season)
        {
            return season.SeriesId != Guid.Empty && await _database.WasLibraryItemRecentlyAnnouncedAsync(season.SeriesId, ParentAnnouncementWindow, cancellationToken).ConfigureAwait(false);
        }

        if (item is Episode episode)
        {
            return (episode.SeriesId != Guid.Empty && await _database.WasLibraryItemRecentlyAnnouncedAsync(episode.SeriesId, ParentAnnouncementWindow, cancellationToken).ConfigureAwait(false))
                || (episode.SeasonId != Guid.Empty && await _database.WasLibraryItemRecentlyAnnouncedAsync(episode.SeasonId, ParentAnnouncementWindow, cancellationToken).ConfigureAwait(false));
        }

        return false;
    }

    internal static bool HasNewParentInBatch(BaseItem item, ISet<Guid> newSeries, ISet<Guid> newSeasons)
        => item switch
        {
            Season season => newSeries.Contains(season.SeriesId),
            Episode episode => newSeries.Contains(episode.SeriesId) || newSeasons.Contains(episode.SeasonId),
            _ => false,
        };

    private async Task QueueAnnouncementAsync(AnnouncementCandidate candidate, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var item = candidate.Item;

        var isEpisode = item is Episode;
        if (config is null
            || (isEpisode && !config.NewEpisodeNotificationsEnabled)
            || (!isEpisode && !config.NewMediaNotificationsEnabled)
            || !ulong.TryParse(config.NewMediaChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out var channelId))
        {
            await MarkAnnouncedAsync(candidate, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? imagePath = null;
        try
        {
            imagePath = await PreparePosterAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailure(_logger, exception);
        }
        var title = item switch
        {
            Episode => "📺 " + (config.Language == "en" ? "New episode" : "Neue Episode"),
            Season => "📺 " + (config.Language == "en" ? "New season" : "Neue Staffel"),
            _ => "🆕 " + (config.Language == "en" ? "New on Jellyfin" : "Neu auf Jellyfin"),
        };
        var description = BuildDescription(item, config.Language);
        var payload = JsonSerializer.Serialize(new
        {
            title,
            description,
            color = 0x00A4DCu,
            attachmentPath = imagePath,
        });
        await _database.EnqueueNotificationAsync(
            NotificationPriority.Low,
            "library",
            $"channel:{channelId.ToString(CultureInfo.InvariantCulture)}",
            payload,
            $"library:{candidate.Identity}",
            DateTime.UtcNow,
            cancellationToken).ConfigureAwait(false);
        await MarkAnnouncedAsync(candidate, cancellationToken).ConfigureAwait(false);
        try
        {
            await _database.WriteAuditAsync("bot", "jellix", "library-announcement-queued", "item", item.Id.ToString("N"), true, item.Name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogFailure(_logger, exception);
        }
    }

    private async Task MarkAnnouncedAsync(AnnouncementCandidate candidate, CancellationToken cancellationToken)
    {
        await _database.MarkLibraryItemAnnouncedAsync(candidate.Item.Id, cancellationToken).ConfigureAwait(false);
        await _database.MarkLibraryIdentityAnnouncedAsync(candidate.Identity, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> PreparePosterAsync(BaseItem original, CancellationToken cancellationToken)
    {
        BaseItem item = original;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            item = _libraryManager.GetItemById(original.Id) ?? original;
            if (!item.HasImage(ImageType.Primary) && item is Episode episode && episode.SeriesId != Guid.Empty)
            {
                item = _libraryManager.GetItemById(episode.SeriesId) ?? item;
            }
            else if (!item.HasImage(ImageType.Primary) && item is Season season && season.SeriesId != Guid.Empty)
            {
                item = _libraryManager.GetItemById(season.SeriesId) ?? item;
            }

            if (item.HasImage(ImageType.Primary)) break;
            if (attempt < 5) await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        if (!item.HasImage(ImageType.Primary))
        {
            return null;
        }

        var image = item.GetImageInfo(ImageType.Primary, 0);
        var result = await _imageProcessor.ProcessImage(new ImageProcessingOptions
        {
            Item = item,
            ItemId = item.Id,
            Image = image,
            ImageIndex = 0,
            MaxWidth = 600,
            MaxHeight = 900,
            Quality = 85,
            SupportedOutputFormats = _imageProcessor.GetSupportedImageOutputFormats(),
        }).ConfigureAwait(false);
        return File.Exists(result.Item1) ? result.Item1 : null;
    }

    private string BuildDescription(BaseItem item, string language)
    {
        if (item is Episode episode)
        {
            var seasonNumber = episode.ParentIndexNumber.GetValueOrDefault();
            var number = episode.IndexNumber.GetValueOrDefault();
            return Limit($"**{Escape(episode.SeriesName ?? episode.Name)}**\nS{seasonNumber:00}E{number:00} — {Escape(episode.Name)}", 4096);
        }

        if (item is Season season)
        {
            var series = season.SeriesId == Guid.Empty ? null : _libraryManager.GetItemById(season.SeriesId);
            var seriesName = series?.Name ?? season.SeriesName ?? season.Name;
            var seasonLabel = language == "en" ? "Season" : "Staffel";
            var seasonName = season.IndexNumber.HasValue ? $"{seasonLabel} {season.IndexNumber.Value}" : season.Name;
            return Limit($"**{Escape(seriesName)}**\n{Escape(seasonName)}", 4096);
        }

        var lines = new List<string> { $"**{Escape(item.Name)}**" };
        if (item.CommunityRating.HasValue) lines.Add($"⭐ {item.CommunityRating.Value:0.0}/10");
        if (item.ProductionYear.HasValue) lines.Add($"📅 {item.ProductionYear.Value}");
        if (item.RunTimeTicks.HasValue) lines.Add($"⏱️ {TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalMinutes:0} min");
        if (!string.IsNullOrWhiteSpace(item.Overview))
        {
            var overview = item.Overview.Length > 500 ? item.Overview[..500] + "…" : item.Overview;
            lines.Add(string.Empty);
            lines.Add(Escape(overview));
        }

        return Limit(string.Join('\n', lines), 4096);
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal).Replace("~", "\\~", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Limit(string value, int length)
        => value.Length <= length ? value : value[..(length - 1)] + "…";

    private static int NotificationOrder(BaseItem item)
        => item switch { Series => 0, Season => 1, Movie => 2, Episode => 3, _ => 4 };

    internal static string BuildStableIdentity(BaseItem item)
    {
        var kind = item switch { Movie => "movie", Series => "series", Season => "season", Episode => "episode", _ => item.GetType().Name.ToLowerInvariant() };
        var position = item switch
        {
            Season season => $"|season:{season.IndexNumber.GetValueOrDefault()}",
            Episode episode => $"|season:{episode.ParentIndexNumber.GetValueOrDefault()}|episode:{episode.IndexNumber.GetValueOrDefault()}",
            _ => string.Empty,
        };
        string source;
        if (item.ProviderIds.Count > 0)
        {
            source = kind + "|" + string.Join('|', item.ProviderIds.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => pair.Key.ToLowerInvariant() + ":" + pair.Value.Trim().ToLowerInvariant())) + position;
        }
        else if (!string.IsNullOrWhiteSpace(item.Path))
        {
            source = kind + "|" + item.Path.Trim().Replace('\\', '/') + position;
        }
        else
        {
            source = $"{item.GetType().Name}|{item.ParentId:N}|{item.Name}|{item.ProductionYear.GetValueOrDefault()}{position}";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private sealed record AnnouncementCandidate(BaseItem Item, string Identity);
}
