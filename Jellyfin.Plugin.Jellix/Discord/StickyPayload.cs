using Discord;

namespace Jellyfin.Plugin.Jellix.Discord;

internal sealed class StickyPayload
{
    public string Content { get; set; } = string.Empty;

    public List<StickyEmbed> Embeds { get; set; } = [];

    public List<string> AttachmentUrls { get; set; } = [];

    public static StickyPayload FromMessage(IMessage message)
        => new()
        {
            Content = message.Content ?? string.Empty,
            Embeds = message.Embeds.Take(10).Select(StickyEmbed.FromEmbed).ToList(),
            AttachmentUrls = message.Attachments.Take(10).Select(attachment => attachment.Url).ToList(),
        };

    public Embed[] BuildEmbeds()
    {
        var result = new List<Embed>();
        var remaining = 6000;
        foreach (var value in Embeds.Take(10))
        {
            if (remaining <= 0) break;
            var built = value.Build(remaining);
            result.Add(built.Embed);
            remaining -= built.Characters;
        }

        return result.ToArray();
    }

    public string BuildContent()
    {
        var attachments = string.Join('\n', AttachmentUrls.Where(IsSafeUrl));
        var safeContent = Content ?? string.Empty;
        var combined = string.IsNullOrEmpty(attachments) ? safeContent : safeContent + (safeContent.Length == 0 ? string.Empty : "\n") + attachments;
        return combined.Length > 2000 ? combined[..2000] : combined;
    }

    private static bool IsSafeUrl(string? value)
        => value?.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

internal sealed class StickyEmbed
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Url { get; set; }
    public uint? Color { get; set; }
    public string? ImageUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? Footer { get; set; }
    public List<StickyEmbedField> Fields { get; set; } = [];

    public static StickyEmbed FromEmbed(IEmbed embed)
        => new()
        {
            Title = embed.Title,
            Description = embed.Description,
            Url = embed.Url,
            Color = embed.Color?.RawValue,
            ImageUrl = embed.Image?.Url,
            ThumbnailUrl = embed.Thumbnail?.Url,
            Footer = embed.Footer?.Text,
            Fields = embed.Fields.Take(25).Select(field => new StickyEmbedField { Name = field.Name, Value = field.Value, Inline = field.Inline }).ToList(),
        };

    public (Embed Embed, int Characters) Build(int characterBudget)
    {
        var builder = new EmbedBuilder();
        var used = 0;
        var title = Take(Title, Math.Min(256, characterBudget - used));
        if (title.Length > 0) { builder.WithTitle(title); used += title.Length; }
        var description = Take(Description, Math.Min(4096, characterBudget - used));
        if (description.Length > 0) { builder.WithDescription(description); used += description.Length; }
        if (IsSafeUrl(Url)) builder.WithUrl(Url);
        if (Color.HasValue) builder.WithColor(new Color(Color.Value));
        if (IsSafeUrl(ImageUrl)) builder.WithImageUrl(ImageUrl);
        if (IsSafeUrl(ThumbnailUrl)) builder.WithThumbnailUrl(ThumbnailUrl);
        var footer = Take(Footer, Math.Min(2048, characterBudget - used));
        if (footer.Length > 0) { builder.WithFooter(footer); used += footer.Length; }
        foreach (var field in Fields.Take(25))
        {
            var available = characterBudget - used;
            if (available < 2) break;
            var name = Take(field.Name, Math.Min(256, available - 1));
            if (name.Length == 0) name = "\u200b";
            available = characterBudget - used - name.Length;
            if (available < 1) break;
            var value = Take(field.Value, Math.Min(1024, available));
            if (value.Length == 0) value = "\u200b";
            builder.AddField(name, value, field.Inline);
            used += name.Length + value.Length;
        }

        return (builder.Build(), used);
    }

    private static string Take(string? value, int maximum)
        => maximum <= 0 || string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= maximum ? value : value[..maximum];

    private static bool IsSafeUrl(string? value)
        => value?.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

internal sealed class StickyEmbedField
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool Inline { get; set; }
}
