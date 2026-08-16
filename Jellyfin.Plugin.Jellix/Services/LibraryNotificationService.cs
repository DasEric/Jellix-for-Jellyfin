using System.Globalization;
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
        SingleReader = false,
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
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode],
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

            foreach (var pending in await _database.ListPendingLibraryAnnouncementsAsync(cancellationToken).ConfigureAwait(false))
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
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ProcessQueueAsync(stoppingToken))).ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        await foreach (var itemId in _items.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                if (await _database.IsLibraryItemKnownAsync(itemId, stoppingToken).ConfigureAwait(false)
                    && !await _database.IsLibraryItemPendingAsync(itemId, stoppingToken).ConfigureAwait(false))
                {
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                await ProcessAsync(itemId, stoppingToken).ConfigureAwait(false);
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
        if (eventArgs.Item is Movie or Series or Episode && !eventArgs.Item.IsVirtualItem)
        {
            _items.Writer.TryWrite(eventArgs.Item.Id);
        }
    }

    private async Task ProcessAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var item = _libraryManager.GetItemById(itemId);
        if (item is null || item.IsVirtualItem)
        {
            if (await _database.IsLibraryItemPendingAsync(itemId, cancellationToken).ConfigureAwait(false))
            {
                await _database.MarkLibraryItemAnnouncedAsync(itemId, cancellationToken).ConfigureAwait(false);
            }

            return;
        }


        var newlyRegistered = await _database.TryRegisterLibraryItemAsync(itemId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (!newlyRegistered && !await _database.IsLibraryItemPendingAsync(itemId, cancellationToken).ConfigureAwait(false)) return;

        var isEpisode = item is Episode;
        if (config is null
            || (isEpisode && !config.NewEpisodeNotificationsEnabled)
            || (!isEpisode && !config.NewMediaNotificationsEnabled)
            || !ulong.TryParse(config.NewMediaChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out var channelId))
        {
            await _database.MarkLibraryItemAnnouncedAsync(item.Id, cancellationToken).ConfigureAwait(false);
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
        var title = isEpisode
            ? "📺 " + (config.Language == "en" ? "New episode" : "Neue Episode")
            : "🆕 " + (config.Language == "en" ? "New on Jellyfin" : "Neu auf Jellyfin");
        var description = BuildDescription(item);
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
            $"library:{item.Id:N}",
            DateTime.UtcNow,
            cancellationToken).ConfigureAwait(false);
        await _database.MarkLibraryItemAnnouncedAsync(item.Id, cancellationToken).ConfigureAwait(false);
        try
        {
            await _database.WriteAuditAsync("bot", "jellix", "library-announcement-queued", "item", item.Id.ToString("N"), true, item.Name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogFailure(_logger, exception);
        }
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

    private static string BuildDescription(BaseItem item)
    {
        if (item is Episode episode)
        {
            var season = episode.ParentIndexNumber.GetValueOrDefault();
            var number = episode.IndexNumber.GetValueOrDefault();
            return Limit($"**{Escape(episode.SeriesName ?? episode.Name)}**\nS{season:00}E{number:00} — {Escape(episode.Name)}", 4096);
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
}
