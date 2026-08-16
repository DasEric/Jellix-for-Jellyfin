using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Models;

namespace Jellyfin.Plugin.Jellix.Services;

public sealed class AccountLinkService
{
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private readonly JellixDatabase _database;

    public AccountLinkService(JellixDatabase database)
    {
        _database = database;
    }

    public async Task<(string Code, DateTime ExpiresUtc)> CreateCodeAsync(Guid jellyfinUserId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Jellix configuration unavailable.");
        if (!config.SelfLinkEnabled)
        {
            throw new InvalidOperationException(Text("Self-Linking ist deaktiviert.", "Self-linking is disabled."));
        }

        if (await _database.FindLinkByJellyfinAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException(Text("Dieses Jellyfin-Konto ist bereits verbunden.", "This Jellyfin account is already linked."));
        }

        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);
        var chars = new char[9];
        for (var i = 0; i < 8; i++)
        {
            chars[i + (i >= 4 ? 1 : 0)] = Alphabet[random[i] % Alphabet.Length];
        }

        chars[4] = '-';
        var code = new string(chars);
        var expiresUtc = DateTime.UtcNow.AddMinutes(Math.Clamp(config.LinkCodeLifetimeMinutes, 1, 30));
        await _database.ReplaceLinkCodeAsync(jellyfinUserId, HashCode(code), expiresUtc, cancellationToken).ConfigureAwait(false);
        return (code, expiresUtc);
    }

    public async Task<UserLink> ConsumeCodeAsync(string code, string guildId, string discordUserId, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.SelfLinkEnabled != true)
        {
            throw new InvalidOperationException(Text("Self-Linking ist deaktiviert.", "Self-linking is disabled."));
        }

        var normalized = NormalizeCode(code);
        if (normalized.Length != 8)
        {
            throw new InvalidOperationException(Text("Der Verbindungscode ist ungültig oder abgelaufen.", "Invalid or expired link code."));
        }

        if (await _database.FindLinkByDiscordAsync(guildId, discordUserId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException(Text("Dieses Discord-Konto ist bereits verbunden.", "This Discord account is already linked."));
        }

        var jellyfinUserId = await _database.ConsumeLinkCodeAsync(HashCode(normalized), guildId, discordUserId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(Text("Der Verbindungscode ist ungültig oder abgelaufen.", "Invalid or expired link code."));
        return await _database.FindLinkByJellyfinAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(Text("Die Verbindung konnte nicht erstellt werden.", "The link could not be created."));
    }

    internal static byte[] HashCode(string code)
        => SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCode(code)));

    private static string NormalizeCode(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Text(string german, string english)
        => Plugin.Instance?.Configuration.Language == "en" ? english : german;
}
