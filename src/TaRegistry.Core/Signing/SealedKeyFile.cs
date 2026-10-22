using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaRegistry.Core.Signing;

public static class SealedKeyFile
{
    private const int Iterations = 200_000;

    public static void Write(string path, Ed25519Key key, string passphrase)
    {
        RequirePassphrase(passphrase);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = Encrypt(key.Seed, passphrase, salt, nonce);
        var document = new SealedKeyDocument
        {
            Version = 1,
            KeyId = key.KeyId,
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(cipher),
            Public = key.PublicText
        };

        var json = JsonSerializer.Serialize(document, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static Ed25519Key Read(string path, string passphrase)
    {
        RequirePassphrase(passphrase);
        var document = JsonSerializer.Deserialize<SealedKeyDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("The key file is empty.");

        if (document.Version != 1)
            throw new InvalidOperationException("Unsupported key file version.");

        var salt = Convert.FromBase64String(document.Salt);
        var nonce = Convert.FromBase64String(document.Nonce);
        var cipher = Convert.FromBase64String(document.Ciphertext);
        var seed = Decrypt(cipher, passphrase, salt, nonce, document.Iterations);
        var key = Ed25519Key.FromSeed(document.KeyId, seed);
        if (!string.Equals(key.PublicText, document.Public, StringComparison.Ordinal))
            throw new InvalidOperationException("The key file public key does not match the seed.");

        return key;
    }

    public static string ReadPublic(string path)
    {
        var document = JsonSerializer.Deserialize<SealedKeyDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("The key file is empty.");
        return document.Public;
    }

    public static void RequirePassphrase(string passphrase)
    {
        if (Encoding.UTF8.GetByteCount(passphrase) < 12)
            throw new ArgumentException("The signing passphrase must be at least 12 bytes.", nameof(passphrase));
    }

    private static byte[] Encrypt(byte[] seed, string passphrase, byte[] salt, byte[] nonce)
    {
        var key = Derive(passphrase, salt, Iterations);
        var cipher = new byte[seed.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, seed, cipher, tag);
        return cipher.Concat(tag).ToArray();
    }

    private static byte[] Decrypt(byte[] cipherAndTag, string passphrase, byte[] salt, byte[] nonce, int iterations)
    {
        if (cipherAndTag.Length < 17)
            throw new InvalidOperationException("The key file ciphertext is truncated.");

        var cipher = cipherAndTag[..^16];
        var tag = cipherAndTag[^16..];
        var key = Derive(passphrase, salt, iterations);
        var seed = new byte[cipher.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, cipher, tag, seed);
        return seed;
    }

    private static byte[] Derive(string passphrase, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private sealed class SealedKeyDocument
    {
        public int Version { get; set; }
        public string KeyId { get; set; } = "";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Ciphertext { get; set; } = "";
        public string Public { get; set; } = "";
    }
}
