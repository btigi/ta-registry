using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace TaRegistry.Core.Signing;

public sealed class Ed25519Key
{
    private Ed25519Key(string keyId, byte[] seed, byte[] publicKey)
    {
        KeyId = keyId;
        Seed = seed;
        PublicKey = publicKey;
    }

    public string KeyId { get; }
    public byte[] Seed { get; }
    public byte[] PublicKey { get; }

    public string PublicText => "ed25519:" + Convert.ToBase64String(PublicKey);

    public string Fingerprint => KeyFingerprint.Format(PublicKey);

    public static Ed25519Key Generate(string keyId)
    {
        RequireKeyId(keyId);
        var seed = RandomNumberGenerator.GetBytes(32);
        return FromSeed(keyId, seed);
    }

    public static Ed25519Key FromSeed(string keyId, byte[] seed)
    {
        RequireKeyId(keyId);
        if (seed.Length != 32)
            throw new ArgumentException("An Ed25519 seed is 32 bytes.", nameof(seed));

        var priv = new Ed25519PrivateKeyParameters(seed, 0);
        var pub = priv.GeneratePublicKey().GetEncoded();
        return new Ed25519Key(keyId, seed.ToArray(), pub);
    }

    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        var priv = new Ed25519PrivateKeyParameters(Seed, 0);
        var signer = new Ed25519Signer();
        signer.Init(true, priv);
        var bytes = message.ToArray();
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return signer.GenerateSignature();
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
            return false;

        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray(), 0));
        var bytes = message.ToArray();
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return signer.VerifySignature(signature.ToArray());
    }

    public static bool TryParsePublic(string text, out byte[] publicKey)
    {
        publicKey = [];
        const string prefix = "ed25519:";
        if (text.Length != prefix.Length + 44 || !text.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        try
        {
            var bytes = Convert.FromBase64String(text[prefix.Length..]);
            if (bytes.Length != 32)
                return false;
            publicKey = bytes;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string SignatureFile(string keyId, byte[] signature)
    {
        var body = Convert.ToBase64String(signature);
        if (body.Length != 88)
            throw new InvalidOperationException("Ed25519 signatures are 88 base64 characters.");

        return $"ed25519 {keyId} {body}\n";
    }

    public static void RequireKeyId(string keyId)
    {
        if (keyId.Length is < 1 or > 64)
            throw new ArgumentException("Key id must be 1 to 64 bytes.", nameof(keyId));

        if (keyId[0] is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            throw new ArgumentException("Key id must start with a-z or 0-9.", nameof(keyId));

        foreach (var c in keyId)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
                throw new ArgumentException("Key id contains a character the descriptor refuses.", nameof(keyId));
        }
    }
}

public static class KeyFingerprint
{
    public static string Format(byte[] publicKey)
    {
        var hash = SHA256.HashData(publicKey);
        var hex = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        return $"{hex[..4]} \u00b7 {hex[4..8]} \u00b7 {hex[8..12]} \u00b7 {hex[12..16]}";
    }
}
