using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Models;
using Jellyfin.Plugin.Jellix.Services;

namespace Jellyfin.Plugin.Jellix.Discord;

internal interface IManagedMessageTransport
{
    Task<bool> ExistsAsync(string id, string key, CancellationToken cancellationToken);
    Task<string?> FindAsync(string key, DateTime sinceUtc, CancellationToken cancellationToken);
    Task<string> CreateAsync(string key, string payload, CancellationToken cancellationToken);
    Task UpdateAsync(string id, string key, string payload, CancellationToken cancellationToken);
}

/// <summary>Persists intent before sending, recovers uncertain creates, and edits rather than reposting.</summary>
internal sealed class ManagedMessagePublisher(JellixDatabase database)
{
    internal async Task PublishAsync(string key, string channelId, string payload, IManagedMessageTransport transport, CancellationToken cancellationToken)
    {
        var state = await database.GetManagedMessageAsync(key, cancellationToken).ConfigureAwait(false);
        var hash = LibraryMedia.Hash(payload);
        if (state is null)
        {
            state = new ManagedMessageRecord(key, channelId, null, string.Empty, DateTime.UtcNow);
            await database.SaveManagedMessageAsync(state, cancellationToken).ConfigureAwait(false);
        }
        else if (state.ChannelId != channelId) throw new ArgumentException("Managed message channel mismatch.", nameof(channelId));

        var id = state.MessageId;
        if (id is not null && !await transport.ExistsAsync(id, key, cancellationToken).ConfigureAwait(false))
        {
            state = state with { MessageId = null, ContentHash = string.Empty, AttemptedUtc = DateTime.UtcNow };
            await database.SaveManagedMessageAsync(state, cancellationToken).ConfigureAwait(false);
            id = null;
        }

        if (id is null)
        {
            id = await transport.FindAsync(key, state.AttemptedUtc.AddMinutes(-1), cancellationToken).ConfigureAwait(false);
            if (id is null) id = await transport.CreateAsync(key, payload, cancellationToken).ConfigureAwait(false);
            else await transport.UpdateAsync(id, key, payload, cancellationToken).ConfigureAwait(false);
        }
        else if (state.ContentHash != hash)
            await transport.UpdateAsync(id, key, payload, cancellationToken).ConfigureAwait(false);

        await database.SaveManagedMessageAsync(state with { MessageId = id, ContentHash = hash }, cancellationToken).ConfigureAwait(false);
    }
}
