using Discord;

namespace Jellyfin.Plugin.Jellix.Discord;

/// <summary>Shared visual language for every Jellix-owned Discord embed.</summary>
internal static class JellixEmbeds
{
    internal static readonly Color Primary = new(0x00A4DC);
    internal static readonly Color Secondary = new(0x7C4DFF);
    internal static readonly Color Success = new(0x2ECC71);
    internal static readonly Color Warning = new(0xF39C12);
    internal static readonly Color Danger = new(0xE74C3C);
    internal static readonly Color Gold = new(0xF1C40F);
    internal static readonly Color Private = new(0x34495E);

    internal static EmbedBuilder Create(string title, string? description = null, Color? color = null, string? footer = null)
    {
        var builder = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(color ?? Primary)
            .WithAuthor("Jellix • Jellyfin Companion")
            .WithFooter(footer ?? DiscordText.T("Jellix für Jellyfin", "Jellix for Jellyfin"))
            .WithCurrentTimestamp();

        if (!string.IsNullOrWhiteSpace(description)) builder.WithDescription(description);
        return builder;
    }
}
