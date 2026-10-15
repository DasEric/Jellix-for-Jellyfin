using System.Reflection;
using Jellyfin.Plugin.Jellix.Discord;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellix.Tests;

public class LibraryProxy : DispatchProxy
{
    public Dictionary<Guid, BaseItem> Items { get; } = [];
    public List<VirtualFolderInfo> Folders { get; } = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.Name switch
        {
            "GetItemById" => Items.GetValueOrDefault((Guid)args![0]!),
            "GetItemList" => Items.Values.ToArray(),
            "GetVirtualFolders" => Folders,
            _ => null,
        };

    public static (ILibraryManager Library, LibraryProxy Proxy) Create()
    {
        var library = Create<ILibraryManager, LibraryProxy>();
        return (library, (LibraryProxy)library);
    }
}

internal sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class MemoryManagedTransport : IManagedMessageTransport
{
    internal Dictionary<string, (string Key, string Payload)> Messages { get; } = [];
    internal int Creates { get; private set; }
    internal int Updates { get; private set; }
    internal bool FailAfterCreate { get; set; }

    public Task<bool> ExistsAsync(string id, string key, CancellationToken cancellationToken)
        => Task.FromResult(Messages.TryGetValue(id, out var message) && message.Key == key);

    internal string? RecoveryPayload { get; private set; }

    public Task<string?> FindAsync(string key, string? attemptedPayload, DateTime sinceUtc, CancellationToken cancellationToken)
    {
        RecoveryPayload = attemptedPayload;
        return Task.FromResult<string?>(Messages.FirstOrDefault(pair => pair.Value.Key == key).Key);
    }

    public Task<string> CreateAsync(string key, string payload, CancellationToken cancellationToken)
    {
        Creates++;
        var id = Creates.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Messages[id] = (key, payload);
        if (FailAfterCreate) { FailAfterCreate = false; throw new IOException("Discord accepted the message, but the response was lost."); }
        return Task.FromResult(id);
    }

    public Task UpdateAsync(string id, string key, string payload, CancellationToken cancellationToken)
    {
        Updates++;
        Messages[id] = (key, payload);
        return Task.CompletedTask;
    }
}
