using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Jellix.Security;

/// <summary>Encrypted storage for the Discord bot token.</summary>
public sealed class SecretStore
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly object _sync = new();
    private readonly string _keyPath;
    private readonly string _tokenPath;

    public SecretStore(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _keyPath = Path.Combine(dataPath, "jellix-secret.key");
        _tokenPath = Path.Combine(dataPath, "discord-token.bin");
    }

    public bool HasToken
    {
        get
        {
            lock (_sync)
            {
                try
                {
                    return File.Exists(_tokenPath) && new FileInfo(_tokenPath).Length > NonceSize + TagSize;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }
    }

    public string? GetToken()
    {
        lock (_sync)
        {
            byte[]? key = null;
            byte[]? plaintext = null;
            try
            {
                if (!File.Exists(_tokenPath))
                {
                    return null;
                }

                var payload = File.ReadAllBytes(_tokenPath);
                if (payload.Length <= NonceSize + TagSize)
                {
                    return null;
                }

                key = GetOrCreateKey();
                var nonce = payload.AsSpan(0, NonceSize);
                var tag = payload.AsSpan(NonceSize, TagSize);
                var ciphertext = payload.AsSpan(NonceSize + TagSize);
                plaintext = new byte[ciphertext.Length];
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(Plugin.PluginGuid));
                return Encoding.UTF8.GetString(plaintext);
            }
            catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                if (key is not null) CryptographicOperations.ZeroMemory(key);
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public void SetToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(token));
        }

        lock (_sync)
        {
            var key = GetOrCreateKey(replaceInvalid: true);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var plaintext = Encoding.UTF8.GetBytes(token.Trim());
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(Plugin.PluginGuid));
                var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
                nonce.CopyTo(payload, 0);
                tag.CopyTo(payload, nonce.Length);
                ciphertext.CopyTo(payload, nonce.Length + tag.Length);
                AtomicWrite(_tokenPath, payload);
                RestrictFile(_tokenPath);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }
    }

    public void ClearToken()
    {
        lock (_sync)
        {
            if (File.Exists(_tokenPath))
            {
                File.Delete(_tokenPath);
            }

            if (File.Exists(_keyPath))
            {
                File.Delete(_keyPath);
            }
        }
    }

    private byte[] GetOrCreateKey(bool replaceInvalid = false)
    {
        if (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            if (existing.Length != KeySize)
            {
                CryptographicOperations.ZeroMemory(existing);
                if (replaceInvalid)
                {
                    File.Delete(_keyPath);
                    return CreateKey();
                }

                throw new CryptographicException("Jellix secret key has an invalid size.");
            }

            return existing;
        }

        return CreateKey();
    }

    private byte[] CreateKey()
    {
        var key = RandomNumberGenerator.GetBytes(KeySize);
        AtomicWrite(_keyPath, key);
        RestrictFile(_keyPath);
        return key;
    }

    private static void AtomicWrite(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Secret directory unavailable.");
        var temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
