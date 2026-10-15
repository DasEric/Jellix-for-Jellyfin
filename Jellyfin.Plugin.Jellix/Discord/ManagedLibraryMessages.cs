using System.Globalization;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Jellyfin.Plugin.Jellix.Configuration;
using Jellyfin.Plugin.Jellix.Models;

namespace Jellyfin.Plugin.Jellix.Discord;

public sealed partial class DiscordBotService
{
    private readonly SemaphoreSlim _managedMessageLock = new(1, 1);

    internal static bool IsManagedLibraryFooter(string? footer)
        => footer is "Jellix • Bibliotheksübersicht" or "Jellix • Library overview" or "Jellix • Neu auf Jellyfin" or "Jellix • New on Jellyfin"
            || footer?.StartsWith("Jellix · overview:", StringComparison.Ordinal) == true
            || footer?.StartsWith("Jellix · series:", StringComparison.Ordinal) == true;

    internal static string LibraryFooter(string key)
        => key.StartsWith("overview:", StringComparison.Ordinal)
            ? DiscordText.T("Jellix • Bibliotheksübersicht", "Jellix • Library overview")
            : DiscordText.T("Jellix • Neu auf Jellyfin", "Jellix • New on Jellyfin");

    internal static bool MatchesManagedSnapshot(IEmbed embed, string key, string? payload)
    {
        if (embed.Footer?.Text == "Jellix · " + key) return true; // v0.4 uncertain-create recovery
        if (payload is null || !ExpectedLibraryFooter(embed.Footer?.Text, key)) return false;
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        return embed.Title == root.GetProperty("title").GetString()
            && embed.Description == root.GetProperty("description").GetString();
    }

    private static bool ExpectedLibraryFooter(string? footer, string key)
        => footer == "Jellix · " + key || (key.StartsWith("overview:", StringComparison.Ordinal)
            ? footer is "Jellix • Bibliotheksübersicht" or "Jellix • Library overview"
            : footer is "Jellix • Neu auf Jellyfin" or "Jellix • New on Jellyfin");

    internal static Embed RepairLibraryFooter(IEmbed embed, string key)
        => embed.Footer?.Text == "Jellix · " + key
            ? embed.ToEmbedBuilder().WithFooter(LibraryFooter(key)).Build() : embed.ToEmbedBuilder().Build();

    internal static bool ShouldDeliverManagedLibrary(string kind, string destination, PluginConfiguration? config)
        => config?.BotEnabled == true && (kind switch
        {
            "library-overview" => config.LibraryOverviewEnabled && destination == "channel:" + config.LibraryOverviewChannelId,
            "library-series" => (config.NewMediaNotificationsEnabled || config.NewEpisodeNotificationsEnabled) && destination == "channel:" + config.NewMediaChannelId,
            _ => false,
        });

    private async Task SendManagedLibraryNotificationAsync(NotificationJob job, CancellationToken cancellationToken)
    {
        await _managedMessageLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SendManagedLibraryCoreAsync(job, cancellationToken).ConfigureAwait(false); }
        finally { _managedMessageLock.Release(); }
    }

    private async Task SendManagedLibraryCoreAsync(NotificationJob job, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (!ShouldDeliverManagedLibrary(job.Kind, job.Destination, config)) return;
        using var document = JsonDocument.Parse(job.PayloadJson);
        var key = document.RootElement.GetProperty("managedKey").GetString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 160
            || !(key.StartsWith("overview:", StringComparison.Ordinal) || key.StartsWith("series:", StringComparison.Ordinal)))
            throw new ArgumentException("Invalid managed library message key.", nameof(job));
        if (!ulong.TryParse(job.Destination.AsSpan("channel:".Length), out var channelId)
            || !ulong.TryParse(config!.GuildId, out var guildId)
            || _client.GetChannel(channelId) is not SocketTextChannel channel || channel.Guild.Id != guildId)
            throw new InvalidOperationException("Managed library channel is unavailable in the configured guild.");
        var permissions = channel.Guild.CurrentUser.GetPermissions(channel);
        if (!permissions.ViewChannel || !permissions.SendMessages || !permissions.EmbedLinks || !permissions.ReadMessageHistory)
            throw new InvalidOperationException("Managed library messages require View Channel, Send Messages, Embed Links and Read Message History.");
        var otherMessages = (await _database.ListManagedMessagesAsync(cancellationToken).ConfigureAwait(false))
            .Where(state => state.Key != key && state.ChannelId == channelId.ToString(CultureInfo.InvariantCulture) && state.MessageId is not null)
            .Select(state => state.MessageId!).ToHashSet(StringComparer.Ordinal);
        await new ManagedMessagePublisher(_database).PublishAsync(key, channelId.ToString(CultureInfo.InvariantCulture), job.PayloadJson,
            new DiscordManagedTransport(await _client.Rest.GetChannelAsync(channelId).ConfigureAwait(false) as IMessageChannel
                ?? throw new InvalidOperationException("Managed library REST channel unavailable."), _client.CurrentUser.Id, permissions.AttachFiles, otherMessages), cancellationToken).ConfigureAwait(false);
    }

    private sealed class DiscordManagedTransport(IMessageChannel channel, ulong botId, bool attachFiles, HashSet<string> otherMessages) : IManagedMessageTransport
    {
        private static RequestOptions Options(CancellationToken token) => new() { CancelToken = token };

        private static bool MatchesKnown(IMessage message, string key, ulong authorId)
            => message.Author.Id == authorId && message.Embeds.Any(embed => ExpectedLibraryFooter(embed.Footer?.Text, key));

        public async Task<bool> ExistsAsync(string id, string key, CancellationToken cancellationToken)
        {
            var message = await channel.GetMessageAsync(ulong.Parse(id, CultureInfo.InvariantCulture), options: Options(cancellationToken)).ConfigureAwait(false);
            return message is not null && MatchesKnown(message, key, botId);
        }

        public async Task<string?> FindAsync(string key, string? attemptedPayload, DateTime sinceUtc, CancellationToken cancellationToken)
        {
            ulong? before = null;
            string? found = null;
            while (true)
            {
                var pages = before.HasValue
                    ? channel.GetMessagesAsync(before.Value, Direction.Before, 100, options: Options(cancellationToken))
                    : channel.GetMessagesAsync(100, options: Options(cancellationToken));
                var messages = (await pages.FlattenAsync().ConfigureAwait(false)).OrderByDescending(message => message.Id).ToArray();
                foreach (var message in messages.Where(message => message.Timestamp.UtcDateTime >= sinceUtc))
                {
                    if (message.Author.Id != botId || otherMessages.Contains(message.Id.ToString(CultureInfo.InvariantCulture))
                        || !message.Embeds.Any(embed => MatchesManagedSnapshot(embed, key, attemptedPayload))) continue;
                    if (found is not null) throw new InvalidOperationException("Ambiguous managed message recovery; no duplicate will be created.");
                    found = message.Id.ToString(CultureInfo.InvariantCulture);
                }
                if (messages.Length == 0 || messages[^1].Timestamp.UtcDateTime < sinceUtc) return found;
                before = messages[^1].Id;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private static EmbedBuilder Build(string key, JsonElement root)
            => JellixEmbeds.Create(root.GetProperty("title").GetString()!, root.GetProperty("description").GetString(), JellixEmbeds.Primary, LibraryFooter(key));

        public async Task<string> CreateAsync(string key, string payload, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(payload);
            var builder = Build(key, document.RootElement);
            var path = document.RootElement.TryGetProperty("attachmentPath", out var attachment) ? attachment.GetString() : null;
            IUserMessage message;
            if (attachFiles && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                builder.WithImageUrl("attachment://" + Path.GetFileName(path));
                message = await channel.SendFileAsync(path, embed: builder.Build(), allowedMentions: AllowedMentions.None, options: Options(cancellationToken)).ConfigureAwait(false);
            }
            else message = await channel.SendMessageAsync(embed: builder.Build(), allowedMentions: AllowedMentions.None, options: Options(cancellationToken)).ConfigureAwait(false);
            return message.Id.ToString(CultureInfo.InvariantCulture);
        }

        public async Task UpdateAsync(string id, string key, string payload, CancellationToken cancellationToken)
        {
            var message = await channel.GetMessageAsync(ulong.Parse(id, CultureInfo.InvariantCulture), options: Options(cancellationToken)).ConfigureAwait(false) as IUserMessage
                ?? throw new InvalidOperationException("Managed library message disappeared before update.");
            if (!MatchesKnown(message, key, botId)) throw new InvalidOperationException("Managed message identity changed.");
            using var document = JsonDocument.Parse(payload);
            var builder = Build(key, document.RootElement);
            var image = message.Attachments.FirstOrDefault()?.Url;
            if (image is not null) builder.WithImageUrl(image);
            await message.ModifyAsync(properties => { properties.Embeds = new[] { builder.Build() }; properties.AllowedMentions = AllowedMentions.None; }, Options(cancellationToken)).ConfigureAwait(false);
        }
    }

    private async Task RepairLegacyLibraryFootersAsync()
    {
        // Ready events and the outbox worker can overlap. Never overwrite a newer payload with an old migration snapshot.
        await _managedMessageLock.WaitAsync().ConfigureAwait(false);
        try { await RepairLegacyLibraryFootersCoreAsync().ConfigureAwait(false); }
        finally { _managedMessageLock.Release(); }
    }

    private async Task RepairLegacyLibraryFootersCoreAsync()
    {
        // Include completed series messages that have no pending outbox job. Never repost old history.
        foreach (var state in await _database.ListManagedMessagesAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (state.MessageId is null || !ulong.TryParse(state.ChannelId, out var channelId)
                || !ulong.TryParse(state.MessageId, out var messageId)) continue;
            var marker = "human-footer-v1:" + state.MessageId;
            if (await _database.GetBotStateAsync(marker, CancellationToken.None).ConfigureAwait(false) == "1") continue;
            try
            {
                if (await _client.Rest.GetChannelAsync(channelId).ConfigureAwait(false) is not ITextChannel channel
                    || channel.GuildId.ToString(CultureInfo.InvariantCulture) != Plugin.Instance?.Configuration.GuildId) continue;
                if (await channel.GetMessageAsync(messageId).ConfigureAwait(false) is IUserMessage message && message.Author.Id == _client.CurrentUser.Id)
                {
                    var embeds = message.Embeds.Select(embed => RepairLibraryFooter(embed, state.Key)).ToArray();
                    if (message.Embeds.Any(embed => embed.Footer?.Text == "Jellix · " + state.Key))
                        await message.ModifyAsync(properties => { properties.Embeds = embeds; properties.AllowedMentions = AllowedMentions.None; }).ConfigureAwait(false);
                }
                await _database.SetBotStateAsync(marker, "1", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) { LogDiscordWarning(_logger, "library footer migration will retry on reconnect", exception); }
        }
    }
}
