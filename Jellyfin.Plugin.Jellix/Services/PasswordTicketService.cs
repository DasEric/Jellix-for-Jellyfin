using System.Security.Cryptography;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Models;

namespace Jellyfin.Plugin.Jellix.Services;

public sealed class PasswordTicketService
{
    private readonly JellixDatabase _database;

    public PasswordTicketService(JellixDatabase database)
    {
        _database = database;
    }

    public async Task<(string Token, DateTime ExpiresUtc)> CreateAsync(Guid jellyfinUserId, string discordUserId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Jellix configuration unavailable.");
        if (!config.PasswordChangeEnabled)
        {
            throw new InvalidOperationException("Password changes are disabled.");
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiresUtc = DateTime.UtcNow.AddMinutes(Math.Clamp(config.PasswordTicketLifetimeMinutes, 1, 30));
        await _database.CreatePasswordTicketAsync(HashToken(token), jellyfinUserId, discordUserId, expiresUtc, cancellationToken).ConfigureAwait(false);
        return (token, expiresUtc);
    }

    public Task<PasswordTicket?> GetAsync(string token, CancellationToken cancellationToken)
        => _database.GetPasswordTicketAsync(HashToken(token), cancellationToken);

    public Task<bool> ConsumeAsync(string token, CancellationToken cancellationToken)
        => _database.ConsumePasswordTicketAsync(HashToken(token), cancellationToken);

    public static string BuildUrl(string token)
    {
        var raw = Plugin.Instance?.Configuration.JellyfinPublicUrl?.Trim();
        if (!TryGetPublicBaseUri(raw, out var baseUri))
        {
            throw new InvalidOperationException("Configure a public HTTPS Jellyfin URL first.");
        }

        var language = Plugin.Instance?.Configuration.Language == "en" ? "en" : "de";
        var builder = new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/Jellix/PasswordPage",
            Query = "lang=" + language,
            Fragment = Uri.EscapeDataString(token),
        };
        return builder.Uri.AbsoluteUri;
    }

    public static bool IsPublicUrlConfigured(string? value = null)
    {
        var raw = value?.Trim() ?? Plugin.Instance?.Configuration.JellyfinPublicUrl?.Trim();
        return TryGetPublicBaseUri(raw, out _);
    }

    internal static bool TryGetPublicBaseUri(string? raw, out Uri uri)
    {
        var valid = Uri.TryCreate(raw, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback))
            && string.IsNullOrEmpty(parsed.UserInfo)
            && string.IsNullOrEmpty(parsed.Query)
            && string.IsNullOrEmpty(parsed.Fragment);
        uri = valid ? parsed! : null!;
        return valid;
    }

    private static byte[] HashToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            return SHA256.HashData(Array.Empty<byte>());
        }

        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
