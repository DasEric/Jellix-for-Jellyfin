using System.Globalization;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Jellyfin.Plugin.Jellix.Configuration;
using Jellyfin.Plugin.Jellix.Models;

namespace Jellyfin.Plugin.Jellix.Discord;

public sealed partial class DiscordBotService
{
    internal static bool IsManagedLibraryFooter(string? footer)
        => footer?.StartsWith("Jellix · overview:", StringComparison.Ordinal) == true
            || footer?.StartsWith("Jellix · series:", StringComparison.Ordinal) == true;

    internal static bool ShouldDeliverManagedLibrary(string kind, string destination, PluginConfiguration? config)
        => config?.BotEnabled == true && (kind switch
        {
            "library-overview" => config.LibraryOverviewEnabled && destination == "channel:" + config.LibraryOverviewChannelId,
            "library-series" => (config.NewMediaNotificationsEnabled || config.NewEpisodeNotificationsEnabled) && destination == "channel:" + config.NewMediaChannelId,
            _ => false,
        });

    private async Task SendManagedLibraryNotificationAsync(NotificationJob job, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (!ShouldDeliverManagedLibrary(job.Kind, job.Destination, config)) return;
        using var document = JsonDocument.Parse(job.PayloadJson);
        var key = document.RootElement.GetProperty("managedKey").GetString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 160 || !IsManagedLibraryFooter("Jellix · " + key))
            throw new ArgumentException("Invalid managed library message key.", nameof(job));
        if (!ulong.TryParse(job.Destination.AsSpan("channel:".Length), out var channelId)
            || !ulong.TryParse(config!.GuildId, out var guildId)
            || _client.GetChannel(channelId) is not SocketTextChannel channel || channel.Guild.Id != guildId)
            throw new InvalidOperationException("Managed library channel is unavailable in the configured guild.");
        var permissions = channel.Guild.CurrentUser.GetPermissions(channel);
        if (!permissions.ViewChannel || !permissions.SendMessages || !permissions.EmbedLinks || !permissions.ReadMessageHistory)
            throw new InvalidOperationException("Managed library messages require View Channel, Send Messages, Embed Links and Read Message History.");
        await new ManagedMessagePublisher(_database).PublishAsync(key, channelId.ToString(CultureInfo.InvariantCulture), job.PayloadJson,
            new DiscordManagedTransport(await _client.Rest.GetChannelAsync(channelId).ConfigureAwait(false) as IMessageChannel
                ?? throw new InvalidOperationException("Managed library REST channel unavailable."), _client.CurrentUser.Id, permissions.AttachFiles), cancellationToken).ConfigureAwait(false);
    }

    private sealed class DiscordManagedTransport(IMessageChannel channel, ulong botId, bool attachFiles) : IManagedMessageTransport
    {
        private static RequestOptions Options(CancellationToken token) => new() { CancelToken = token };

        private static bool Matches(IMessage message, string key, ulong authorId)
            => message.Author.Id == authorId && message.Embeds.Any(embed => embed.Footer?.Text == "Jellix · " + key);

        public async Task<bool> ExistsAsync(string id, string key, CancellationToken cancellationToken)
        {
            var message = await channel.GetMessageAsync(ulong.Parse(id, CultureInfo.InvariantCulture), options: Options(cancellationToken)).ConfigureAwait(false);
            return message is not null && Matches(message, key, botId);
        }

        public async Task<string?> FindAsync(string key, DateTime sinceUtc, CancellationToken cancellationToken)
        {
            ulong? before = null;
            while (true)
            {
                var pages = before.HasValue
                    ? channel.GetMessagesAsync(before.Value, Direction.Before, 100, options: Options(cancellationToken))
                    : channel.GetMessagesAsync(100, options: Options(cancellationToken));
                var messages = (await pages.FlattenAsync().ConfigureAwait(false)).OrderByDescending(message => message.Id).ToArray();
                foreach (var message in messages)
                    if (Matches(message, key, botId)) return message.Id.ToString(CultureInfo.InvariantCulture);
                if (messages.Length == 0 || messages[^1].Timestamp.UtcDateTime < sinceUtc) return null;
                before = messages[^1].Id;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private static EmbedBuilder Build(string key, JsonElement root)
            => JellixEmbeds.Create(root.GetProperty("title").GetString()!, root.GetProperty("description").GetString(), JellixEmbeds.Primary, "Jellix · " + key);

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
            if (!Matches(message, key, botId)) throw new InvalidOperationException("Managed message identity changed.");
            using var document = JsonDocument.Parse(payload);
            var builder = Build(key, document.RootElement);
            var image = message.Attachments.FirstOrDefault()?.Url;
            if (image is not null) builder.WithImageUrl(image);
            await message.ModifyAsync(properties => { properties.Embeds = new[] { builder.Build() }; properties.AllowedMentions = AllowedMentions.None; }, Options(cancellationToken)).ConfigureAwait(false);
        }
    }
}
