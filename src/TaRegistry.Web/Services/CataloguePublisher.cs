using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Core;
using TaRegistry.Core.Catalogue;
using TaRegistry.Core.Signing;
using TaRegistry.Web.Data;
using TaRegistry.Web.Storage;

namespace TaRegistry.Web.Services;

public sealed record PublishResult(bool Ok, long Sequence, IReadOnlyList<string> Problems);

public sealed class CataloguePublisher(
    RegistryDb db,
    StorageLayout layout,
    SigningKeyService keys)
{
    public async Task<PublishResult> PreviewAsync(CancellationToken cancellationToken)
    {
        var state = await db.CatalogueStates.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var packages = await LoadDraftsAsync(cancellationToken);
        var problems = packages.SelectMany(CatalogueRules.Problems).ToList();
        var next = state?.Json is null ? 1 : state.Sequence + 1;
        return new PublishResult(problems.Count == 0, next, problems);
    }

    public async Task<PublishResult> PublishAsync(CancellationToken cancellationToken)
    {
        var settings = await db.Settings.SingleAsync(cancellationToken);
        var keyRows = await db.SigningKeys.Where(k => k.IncludeInCatalogue).OrderBy(k => k.CreatedAt).ToListAsync(cancellationToken);
        if (keyRows.Count == 0)
            return new PublishResult(false, 0, ["Create a signing key before publishing."]);

        var packages = await db.Packages.OrderBy(p => p.Kind).ThenBy(p => p.PackageId).ToListAsync(cancellationToken);
        var drafts = packages.Select(PackageMaps.ToDraft).ToList();
        var problems = drafts.SelectMany(CatalogueRules.Problems).ToList();
        var descriptorProblems = CatalogueRules.DescriptorProblems(DescriptorService.Model(settings, keyRows));
        problems.AddRange(descriptorProblems);
        if (problems.Count > 0)
            return new PublishResult(false, 0, problems);

        var state = await db.CatalogueStates.SingleOrDefaultAsync(cancellationToken);
        var sequence = state?.Json is null ? 1 : state!.Sequence + 1;
        var now = DateTimeOffset.UtcNow;
        var draft = new CatalogueDraft
        {
            RegistryId = settings.RegistryId,
            Sequence = sequence,
            GeneratedUtc = now,
            ExpiresUtc = now.AddDays(settings.CatalogueExpiryDays),
            Downloads = settings.BaseUrl.TrimEnd('/') + "/",
            Keys = keyRows.Select(k => new PublicKeyRecord { Id = k.KeyId, Public = k.PublicKey }).ToList(),
            Packages = drafts
        };

        var json = CatalogueWriter.Write(draft);
        if (json.Length > 16 * 1024 * 1024)
            return new PublishResult(false, 0, ["The catalogue is larger than 16 MiB."]);

        var signer = await keys.LoadSignerAsync(cancellationToken);
        var signature = signer.Sign(json);
        if (!Ed25519Key.Verify(signer.PublicKey, json, signature))
            return new PublishResult(false, 0, ["The catalogue signature did not verify."]);

        var signatureText = Ed25519Key.SignatureFile(signer.KeyId, signature);
        state ??= db.CatalogueStates.Add(new CatalogueState { Id = 1 }).Entity;
        state.Sequence = sequence;
        state.Json = json;
        state.Signature = signatureText;
        state.Generated = UtcTimes.Format(draft.GeneratedUtc);
        state.Expires = UtcTimes.Format(draft.ExpiresUtc);
        state.Dirty = false;
        state.SignedByKeyId = signer.KeyId;
        foreach (var package in packages)
            package.InCatalogue = true;

        await db.SaveChangesAsync(cancellationToken);
        await new DescriptorService(db, layout).WriteFileAsync(cancellationToken);
        return new PublishResult(true, sequence, []);
    }

    private async Task<List<CataloguePackage>> LoadDraftsAsync(CancellationToken cancellationToken)
    {
        var packages = await db.Packages.AsNoTracking().OrderBy(p => p.Kind).ThenBy(p => p.PackageId).ToListAsync(cancellationToken);
        return packages.Select(PackageMaps.ToDraft).ToList();
    }
}

public static class PackageMaps
{
    public static CataloguePackage ToDraft(PackageEntity package)
    {
        IReadOnlyList<string>? tags = null;
        if (!string.IsNullOrWhiteSpace(package.TagsJson))
            tags = JsonSerializer.Deserialize<List<string>>(package.TagsJson);

        return new CataloguePackage
        {
            Kind = package.Kind,
            Id = package.PackageId,
            Name = package.Name,
            Release = package.Release,
            Size = package.Size,
            Sha256 = package.Sha256,
            Version = package.Version,
            Revision = package.Revision,
            Publisher = package.Publisher,
            Author = package.Author,
            Summary = package.Summary,
            Notes = package.Notes,
            Homepage = package.Homepage,
            Tags = tags,
            Badge = package.BadgeSha is null ? null : "/v1/i/" + package.BadgeSha + ".png",
            Published = package.PublishedDay,
            DetailsJson = package.DetailsJson
        };
    }
}

public sealed class DescriptorService(RegistryDb db, StorageLayout layout)
{
    public async Task<string> TextAsync(CancellationToken cancellationToken)
    {
        var settings = await db.Settings.AsNoTracking().SingleAsync(cancellationToken);
        var keys = await db.SigningKeys.AsNoTracking().Where(k => k.IncludeInCatalogue).OrderBy(k => k.CreatedAt).ToListAsync(cancellationToken);
        return DescriptorWriter.Write(Model(settings, keys));
    }

    public async Task WriteFileAsync(CancellationToken cancellationToken)
    {
        var text = await TextAsync(cancellationToken);
        await File.WriteAllTextAsync(layout.DescriptorFile, text, new UTF8Encoding(false), cancellationToken);
    }

    public static DescriptorModel Model(RegistrySettings settings, IReadOnlyList<SigningKeyEntity> keys)
    {
        var baseUrl = settings.BaseUrl.TrimEnd('/');
        return new DescriptorModel
        {
            Id = settings.RegistryId,
            Name = settings.Name,
            Title = Blank(settings.Title),
            Description = Blank(settings.Description),
            Author = Blank(settings.Author),
            Homepage = Blank(settings.Homepage),
            CatalogueUrl = baseUrl + "/v1/catalogue.json",
            ApiUrl = baseUrl + "/api/v1",
            InstallIdRequired = settings.InstallIdRequired,
            Keys = keys.Select(k => new PublicKeyRecord { Id = k.KeyId, Public = k.PublicKey }).ToList()
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
