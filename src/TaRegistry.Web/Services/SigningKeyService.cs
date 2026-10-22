using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Core.Catalogue;
using TaRegistry.Core.Signing;
using TaRegistry.Web.Data;
using TaRegistry.Web.Storage;

namespace TaRegistry.Web.Services;

public sealed class SigningKeyService(
    RegistryDb db,
    StorageLayout layout,
    Microsoft.Extensions.Options.IOptions<SigningOptions> signingOptions)
{
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (await db.SigningKeys.AnyAsync(cancellationToken))
            return;

        var options = signingOptions.Value;
        var key = Ed25519Key.Generate(options.KeyId);
        SealedKeyFile.Write(layout.KeyPath(key.KeyId), key, options.Passphrase);
        db.SigningKeys.Add(new SigningKeyEntity
        {
            KeyId = key.KeyId,
            PublicKey = key.PublicText,
            IncludeInCatalogue = true,
            IsSigner = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> RotateAsync(CancellationToken cancellationToken)
    {
        var existing = await db.SigningKeys.OrderBy(k => k.CreatedAt).ToListAsync(cancellationToken);
        if (existing.Count >= 8)
            throw new InvalidOperationException("A registry can publish at most 8 signing keys.");

        var signer = existing.Single(k => k.IsSigner);
        var keyId = NextKeyId(signer.KeyId, existing.Select(k => k.KeyId).ToHashSet(StringComparer.Ordinal));
        var key = Ed25519Key.Generate(keyId);
        SealedKeyFile.Write(layout.KeyPath(key.KeyId), key, signingOptions.Value.Passphrase);
        db.SigningKeys.Add(new SigningKeyEntity
        {
            KeyId = key.KeyId,
            PublicKey = key.PublicText,
            IncludeInCatalogue = true,
            IsSigner = false,
            CreatedAt = DateTime.UtcNow
        });
        await MarkDirtyAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return key.KeyId;
    }

    public async Task PromoteAsync(string keyId, CancellationToken cancellationToken)
    {
        var keys = await db.SigningKeys.ToListAsync(cancellationToken);
        var chosen = keys.SingleOrDefault(k => k.KeyId == keyId)
            ?? throw new InvalidOperationException("That signing key is not on this registry.");

        foreach (var key in keys)
            key.IsSigner = key.KeyId == chosen.KeyId;

        await MarkDirtyAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Ed25519Key> LoadSignerAsync(CancellationToken cancellationToken)
    {
        var signer = await db.SigningKeys.AsNoTracking().SingleAsync(k => k.IsSigner, cancellationToken);
        return SealedKeyFile.Read(layout.KeyPath(signer.KeyId), signingOptions.Value.Passphrase);
    }

    private async Task MarkDirtyAsync(CancellationToken cancellationToken)
    {
        var state = await db.CatalogueStates.SingleOrDefaultAsync(cancellationToken);
        if (state is not null)
            state.Dirty = true;
    }

    private static string NextKeyId(string current, HashSet<string> taken)
    {
        var stem = current;
        var dash = current.LastIndexOf('-');
        if (dash > 0 && int.TryParse(current[(dash + 1)..], out _))
            stem = current[..dash];

        for (var n = 2; n < 1000; n++)
        {
            var suffix = "-" + n.ToString();
            var room = 64 - Encoding.UTF8.GetByteCount(suffix);
            var trimmed = stem;
            while (Encoding.UTF8.GetByteCount(trimmed) > room && trimmed.Length > 1)
                trimmed = trimmed[..^1];
            trimmed = trimmed.TrimEnd('-', '.');
            if (trimmed.Length == 0 || !char.IsAsciiLetterOrDigit(trimmed[0]))
                trimmed = "key";

            var candidate = trimmed + suffix;
            if (!taken.Contains(candidate))
                return candidate;
        }

        throw new InvalidOperationException("Could not choose a new key id.");
    }
}
