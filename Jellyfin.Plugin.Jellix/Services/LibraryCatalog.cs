using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellix.Configuration;
using Jellyfin.Plugin.Jellix.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Jellix.Services;

/// <summary>Reads the selected local media libraries, never personal playback totals.</summary>
public sealed class LibraryCatalog : IDisposable
{
    private readonly ILibraryManager _library;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LibraryCounts? _counts;
    private DateTime _countedUtc;
    private string? _countedSelection;
    private readonly object _librarySync = new();
    private IReadOnlyList<VirtualFolderInfo> _libraries = [];
    private DateTime _librariesUtc;

    internal IReadOnlyList<VirtualFolderInfo> AvailableLibraries()
    {
        lock (_librarySync)
        {
            if (DateTime.UtcNow - _librariesUtc > TimeSpan.FromSeconds(30))
            {
                _libraries = _library.GetVirtualFolders().Where(folder =>
                    folder.CollectionType is null or CollectionTypeOptions.mixed or CollectionTypeOptions.movies or CollectionTypeOptions.tvshows).ToArray();
                _librariesUtc = DateTime.UtcNow;
            }
            return _libraries;
        }
    }

    public LibraryCatalog(ILibraryManager library) : this(library, () => Plugin.Instance?.Configuration) { }

    internal LibraryCatalog(ILibraryManager library, Func<PluginConfiguration?> configuration)
    {
        _library = library;
        _configuration = configuration;
    }

    public void Dispose() => _gate.Dispose();

    internal IReadOnlyList<VirtualFolderInfo> SelectedLibraries()
    {
        var ids = (_configuration()?.LibraryIds ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var selected = ids.Select(id => Guid.TryParse(id, out var value) ? value : Guid.Empty).ToHashSet();
        return AvailableLibraries().Where(folder => selected.Count == 0 || (Guid.TryParse(folder.ItemId, out var id) && selected.Contains(id))).ToArray();
    }

    internal string[] MediaRoots() => SelectedLibraries().SelectMany(folder => folder.Locations).Distinct(StringComparer.Ordinal).ToArray();

    internal bool IsSelected(BaseItem item)
        => LibraryMedia.IsLocal(item) && MediaRoots().Any(root => LibraryMedia.IsUnder(item.Path, root));

    internal IReadOnlyList<BaseItem> GetItems()
    {
        var roots = MediaRoots();
        return _library.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
        }).Where(item => LibraryMedia.IsLocal(item) && roots.Any(root => LibraryMedia.IsUnder(item.Path, root))).ToArray();
    }

    internal string SeriesKey(Guid id) => _library.GetItemById(id) is { } series ? LibraryMedia.Identity(series) : "series:" + id.ToString("N");

    public async Task<LibraryCounts> GetCountsAsync(bool refresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var selection = _configuration()?.LibraryIds ?? string.Empty;
            if (refresh || _counts is null || selection != _countedSelection || DateTime.UtcNow - _countedUtc > TimeSpan.FromMinutes(1))
            {
                var keys = new Dictionary<Guid, string>();
                _counts = LibraryMedia.Count(GetItems(), id =>
                {
                    if (!keys.TryGetValue(id, out var key)) keys[id] = key = SeriesKey(id);
                    return key;
                });
                _countedUtc = DateTime.UtcNow;
                _countedSelection = selection;
            }

            return _counts;
        }
        finally { _gate.Release(); }
    }

    internal string[] StoragePaths(PluginConfiguration config)
        => string.IsNullOrWhiteSpace(config.MediaStoragePaths) ? MediaRoots()
            : config.MediaStoragePaths.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
