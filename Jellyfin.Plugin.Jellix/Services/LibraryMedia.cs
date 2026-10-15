using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Jellyfin.Plugin.Jellix.Models;

namespace Jellyfin.Plugin.Jellix.Services;

/// <summary>One set of counting/identity rules for the overview and series announcements.</summary>
internal static class LibraryMedia
{
    internal static bool IsLocal(BaseItem item)
        => !item.IsVirtualItem && !string.IsNullOrWhiteSpace(item.Path) && Path.IsPathFullyQualified(item.Path);

    internal static string Identity(BaseItem item)
    {
        var kind = item switch { Movie => "movie", Series => "series", Season => "season", Episode => "episode", _ => "item" };
        var position = item is Season season ? "|season:" + season.IndexNumber?.ToString(CultureInfo.InvariantCulture) : string.Empty;
        // A preferred provider, rather than the entire mutable provider dictionary.
        foreach (var provider in new[] { "Tmdb", "Tvdb", "Imdb" })
        {
            if (item.ProviderIds.TryGetValue(provider, out var id) && !string.IsNullOrWhiteSpace(id))
                return Hash(kind + "|" + provider.ToLowerInvariant() + ":" + id.Trim().ToLowerInvariant() + position);
        }

        return Hash(kind + "|" + (!string.IsNullOrWhiteSpace(item.Path)
            ? NormalizePath(item.Path)
            : $"{item.ParentId:N}|{item.Name}|{item.ProductionYear.GetValueOrDefault()}") + position);
    }

    internal static string EpisodeIdentity(Episode episode, string seriesKey)
        => episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
            ? Hash($"{seriesKey}|s:{episode.ParentIndexNumber.Value}|e:{episode.IndexNumber.Value}|end:{episode.IndexNumberEnd ?? episode.IndexNumber.Value}")
            : Identity(episode);

    internal static IEnumerable<(string Identity, int? Number)> EpisodePositions(Episode episode, string seriesKey)
    {
        if (episode.ParentIndexNumber.HasValue && episode.IndexNumber is > 0)
        {
            var start = (long)episode.IndexNumber.Value;
            var end = Math.Min(start + 1000, Math.Max(start, episode.IndexNumberEnd ?? episode.IndexNumber.Value));
            for (var number = start; number <= end; number++)
                yield return (Hash($"{seriesKey}|position|s:{episode.ParentIndexNumber.Value}|e:{number}"), (int)number);
        }
        else yield return (Hash("position|" + EpisodeIdentity(episode, seriesKey)), null);
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string NormalizePath(string path)
    {
        var value = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? value.ToUpperInvariant() : value;
    }

    internal static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalized.Equals(parent, comparison) || normalized.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }

    internal static LibraryCounts Count(IEnumerable<BaseItem> items, Func<Guid, string> seriesKey)
    {
        var local = items.Where(IsLocal).ToArray();
        var episodes = local.OfType<Episode>().Where(e => e.SeriesId != Guid.Empty).ToArray();
        return new LibraryCounts(
            local.OfType<Movie>().Select(Identity).Distinct(StringComparer.Ordinal).LongCount(),
            episodes.Select(e => seriesKey(e.SeriesId)).Distinct(StringComparer.Ordinal).LongCount(),
            episodes.GroupBy(e => seriesKey(e.SeriesId), StringComparer.Ordinal).Sum(group => CountEpisodes(group).Episodes));
    }

    internal static (long Episodes, int Seasons, int Specials) CountEpisodes(IEnumerable<Episode> episodes)
    {
        var positions = new HashSet<string>(StringComparer.Ordinal);
        var seasons = new HashSet<string>(StringComparer.Ordinal);
        var specials = new HashSet<string>(StringComparer.Ordinal);
        foreach (var episode in episodes.Where(IsLocal))
        {
            var season = episode.ParentIndexNumber?.ToString(CultureInfo.InvariantCulture)
                ?? (episode.SeasonId != Guid.Empty ? episode.SeasonId.ToString("N") : Path.GetDirectoryName(episode.Path) ?? episode.Id.ToString("N"));
            if (episode.ParentIndexNumber == 0) specials.Add(season);
            else if (episode.ParentIndexNumber is > 0 || episode.SeasonId != Guid.Empty) seasons.Add(season);
            if (episode.IndexNumber is > 0)
            {
                var end = Math.Max((long)episode.IndexNumber.Value, episode.IndexNumberEnd ?? episode.IndexNumber.Value);
                // Bound malformed metadata without allocating an unbounded range.
                end = Math.Min(end, (long)episode.IndexNumber.Value + 1000);
                for (var index = (long)episode.IndexNumber.Value; index <= end; index++) positions.Add($"{season}:{index}");
            }
            else positions.Add(season + ":" + Identity(episode));
        }

        return (positions.Count, seasons.Count, specials.Count);
    }

    internal static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("~", "\\~", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);

    internal static string DescribeSeries(string name, IEnumerable<Episode> episodes, bool english)
        => DescribeSeries(name, CountEpisodes(episodes), english);

    internal static string DescribeSeries(string name, (long Episodes, int Seasons, int Specials) counts, bool english)
    {
        var culture = CultureInfo.GetCultureInfo(english ? "en-US" : "de-DE");
        var seasonLabel = english ? (counts.Seasons == 1 ? "season" : "seasons") : (counts.Seasons == 1 ? "Staffel" : "Staffeln");
        var episodeLabel = english ? (counts.Episodes == 1 ? "episode" : "episodes") : (counts.Episodes == 1 ? "Folge" : "Folgen");
        var extra = counts.Specials > 0 ? (english ? " · including specials" : " · einschließlich Specials") : string.Empty;
        var title = name.Length > 250 ? name[..250] + "…" : name;
        if (counts.Seasons == 0)
            return $"**{Escape(title)}**\n" + (english ? "Newly available: " : "Neu verfügbar: ")
                + $"**{counts.Episodes.ToString("N0", culture)} {episodeLabel}** "
                + (counts.Specials > 0 ? "(Specials)" : english ? "(season not assigned yet)" : "(Staffel noch nicht zugeordnet)");
        return $"**{Escape(title)}**\n" + (english ? "Newly available: " : "Neu verfügbar: ")
            + $"**{counts.Seasons.ToString("N0", culture)} {seasonLabel} ({counts.Episodes.ToString("N0", culture)} {episodeLabel})**{extra}";
    }
}
