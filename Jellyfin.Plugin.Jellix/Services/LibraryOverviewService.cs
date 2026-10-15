using System.Globalization;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellix.Services;

/// <summary>Maintains a single editable Discord overview, independent of static stickies.</summary>
public sealed class LibraryOverviewService : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailure = LoggerMessage.Define(LogLevel.Warning, new EventId(3301, "LibraryOverviewFailed"), "Jellix library overview update failed");
    private readonly LibraryCatalog _catalog;
    private readonly ILibraryManager _library;
    private readonly JellixDatabase _database;
    private readonly ILogger<LibraryOverviewService> _logger;
    private readonly MediaStorageReader _storage = new();
    private long _dirty = 1;
    private DateTime _nextRefresh;
    private string? _lastConfiguration;

    public LibraryOverviewService(LibraryCatalog catalog, ILibraryManager library, JellixDatabase database, ILogger<LibraryOverviewService> logger)
    {
        _catalog = catalog;
        _library = library;
        _database = database;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnChanged;
        _library.ItemUpdated += OnChanged;
        _library.ItemRemoved += OnChanged;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnChanged;
        _library.ItemUpdated -= OnChanged;
        _library.ItemRemoved -= OnChanged;
        return base.StopAsync(cancellationToken);
    }

    private void OnChanged(object? sender, ItemChangeEventArgs args) => Interlocked.Increment(ref _dirty);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                var configurationKey = config is null ? string.Empty : string.Join('|', config.GuildId, config.LibraryOverviewChannelId,
                    config.LibraryIds, config.MediaStoragePaths, config.Language, config.LibraryOverviewMinutes);
                if (config is { BotEnabled: true, LibraryOverviewEnabled: true }
                    && ulong.TryParse(config.GuildId, out _) && ulong.TryParse(config.LibraryOverviewChannelId, out _)
                    && (configurationKey != _lastConfiguration || DateTime.UtcNow >= _nextRefresh || Volatile.Read(ref _dirty) > 0))
                {
                    var generation = Volatile.Read(ref _dirty);
                    var counts = await _catalog.GetCountsAsync(true, stoppingToken).ConfigureAwait(false);
                    var english = config.Language == "en";
                    var description = Describe(counts, _storage.Read(_catalog.StoragePaths(config), english), english);
                    var key = "overview:" + config.GuildId + ":" + config.LibraryOverviewChannelId;
                    var payload = JsonSerializer.Serialize(new { managedKey = key, title = english ? "📚 Jellyfin library" : "📚 Jellyfin-Bibliothek", description, color = 0x00A4DCu });
                    // Even unchanged snapshots reach the sender periodically: it
                    // verifies existence, but does not edit unchanged messages.
                    await _database.QueueOverviewAsync(config.LibraryOverviewChannelId, payload, key, stoppingToken).ConfigureAwait(false);
                    Interlocked.CompareExchange(ref _dirty, 0, generation);
                    _nextRefresh = DateTime.UtcNow.AddMinutes(Math.Clamp(config.LibraryOverviewMinutes, 1, 1440));
                    _lastConfiguration = configurationKey;
                }
                else if (config is not { BotEnabled: true, LibraryOverviewEnabled: true }) _lastConfiguration = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { LogFailure(_logger, exception); }

            // Coalesce library event storms; never enumerate the library in an event callback.
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
    }

    internal static string Describe(LibraryCounts counts, IReadOnlyList<StorageUsage> storage, bool english)
    {
        var culture = CultureInfo.GetCultureInfo(english ? "en-US" : "de-DE");
        var text = new StringBuilder()
            .Append("🎬 **").Append(counts.Movies.ToString("N0", culture)).Append(english ? " movies**\n" : " Filme**\n")
            .Append("📺 **").Append(counts.Series.ToString("N0", culture)).Append(english ? " series (" : " Serien (")
            .Append(counts.Episodes.ToString("N0", culture)).Append(english ? " episodes total)**\n" : " Folgen insgesamt)**\n");
        foreach (var value in storage.Take(12))
        {
            text.Append("\n💾 **").Append(value.Label).Append("**\n");
            if (value.TotalBytes is not > 0 || value.FreeBytes is null) { text.Append(value.Error); continue; }
            var used = value.TotalBytes.Value - value.FreeBytes.Value;
            text.Append(FormatBytes(used, culture)).Append(english ? " of " : " von ").Append(FormatBytes(value.TotalBytes.Value, culture))
                .Append(english ? " used · " : " belegt · ").Append(FormatBytes(value.FreeBytes.Value, culture)).Append(english ? " free" : " frei")
                .Append(" (").Append((100d * used / value.TotalBytes.Value).ToString("0.#", culture)).Append(" %)");
        }

        if (storage.Count == 0) text.Append(english ? "\n💾 No media storage configured." : "\n💾 Kein Medienspeicher konfiguriert.");
        if (storage.Count > 12) text.Append(english ? "\nAdditional storage omitted." : "\nWeitere Datenträger ausgeblendet.");
        var result = text.ToString();
        return result.Length <= 4096 ? result : result[..4095] + "…";
    }

    private static string FormatBytes(long bytes, CultureInfo culture)
        => bytes >= 1_000_000_000_000L ? (bytes / 1_000_000_000_000d).ToString("0.00", culture) + " TB"
            : (bytes / 1_000_000_000d).ToString("0.00", culture) + " GB";
}
