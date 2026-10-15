using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Jellyfin.Plugin.Jellix.Models;

namespace Jellyfin.Plugin.Jellix.Services;

/// <summary>Filesystem capacity, not an expensive recursive sum of video sizes.</summary>
internal sealed class MediaStorageReader
{
    private readonly Dictionary<string, string> _previousMounts = new(StringComparer.Ordinal);

    internal IReadOnlyList<StorageUsage> Read(IEnumerable<string> paths, bool english)
    {
        var result = new List<StorageUsage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var mounts = OperatingSystem.IsWindows() ? [] : ReadMounts();
        foreach (var raw in paths.Distinct(StringComparer.Ordinal))
        {
            var label = (english ? "Media storage " : "Medienspeicher ") + (result.Count + 1).ToString(CultureInfo.InvariantCulture);
            try
            {
                var path = Path.GetFullPath(raw);
                if (!Directory.Exists(path)) throw new IOException("Media directory is unavailable.");
                var probe = OperatingSystem.IsWindows() ? ReadWindows(path) : ReadUnix(path, mounts);
                var mount = probe.Mount;
                if (mount.Format is "overlay" or "overlayfs") throw new IOException("Container overlay is not a media capacity source.");
                if (_previousMounts.TryGetValue(path, out var previous) && previous != mount.Identity)
                    throw new IOException("Media filesystem changed or was unmounted.");
                _previousMounts[path] = mount.Identity;
                if (!seen.Add(mount.Identity)) continue;
                if (probe.Total <= 0) throw new IOException("Media drive is not ready.");
                result.Add(new StorageUsage(label, probe.Total, Math.Clamp(probe.Free, 0, probe.Total), null));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
            {
                result.Add(new StorageUsage(label, null, null, english ? "Storage unavailable" : "Speicher nicht erreichbar"));
            }
        }

        return result;
    }

    internal sealed record Mount(string Path, string Identity, string Format);

    private static (Mount Mount, long Total, long Free) ReadUnix(string path, Mount[] mounts)
    {
        path = ResolveDirectory(path);
        var mount = FindMount(path, mounts) ?? throw new IOException("No matching media filesystem.");
        var drive = new DriveInfo(mount.Path);
        if (!drive.IsReady) throw new IOException("Media drive is not ready.");
        return (mount, drive.TotalSize, drive.TotalFreeSpace);
    }

    private static string ResolveDirectory(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var resolved = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(resolved, part));
            resolved = directory.LinkTarget is null ? directory.FullName
                : directory.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Broken media directory link.");
        }
        return resolved;
    }

    [SupportedOSPlatform("windows")]
    private static (Mount Mount, long Total, long Free) ReadWindows(string path)
    {
        // Resolve the actual volume, including mounted folders/junctions, not
        // simply the drive letter of the media directory.
        var volume = new char[Math.Max(1024, path.Length + 2)];
        if (!GetVolumePathNameW(path, volume, volume.Length)) throw new IOException("Media volume lookup failed.");
        var volumePath = new string(volume, 0, Array.IndexOf(volume, '\0'));
        var volumeName = new char[1024];
        var identity = GetVolumeNameForVolumeMountPointW(volumePath, volumeName, volumeName.Length)
            ? new string(volumeName, 0, Array.IndexOf(volumeName, '\0')).ToUpperInvariant() : LibraryMedia.NormalizePath(volumePath);
        if (!GetDiskFreeSpaceExW(path.TrimEnd('\\') + "\\", out _, out var total, out var free))
            throw new IOException("Media capacity lookup failed.");
        return (new Mount(volumePath, identity, string.Empty), checked((long)total), checked((long)free));
    }

    // https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string path, [Out] char[] volume, int length);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string path, [Out] char[] name, int length);

    internal static Mount? FindMount(string path, IEnumerable<Mount> mounts)
        => mounts.Where(mount => LibraryMedia.IsUnder(path, mount.Path)).OrderByDescending(mount => mount.Path.Length).FirstOrDefault();

    private static Mount[] ReadMounts()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/self/mountinfo"))
        {
            // major:minor plus filesystem source identifies bind mounts of the
            // same filesystem without counting their capacity twice.
            return File.ReadLines("/proc/self/mountinfo").Select(ParseMount).OfType<Mount>().ToArray();
        }

        return DriveInfo.GetDrives().Select(drive => new Mount(drive.Name, LibraryMedia.NormalizePath(drive.Name), string.Empty)).ToArray();
    }

    internal static Mount? ParseMount(string line)
    {
        var parts = line.Split(' ');
        var separator = Array.IndexOf(parts, "-");
        return separator >= 6 && parts.Length > separator + 2
            ? new Mount(UnescapeMount(parts[4]), parts[2] + ":" + parts[separator + 2], parts[separator + 1]) : null;
    }

    private static string UnescapeMount(string value)
        => value.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
}
