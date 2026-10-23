using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Core;
using TaRegistry.Core.Catalogue;
using TaRegistry.Core.Packages;
using TaRegistry.Web.Data;
using TaRegistry.Web.Storage;

namespace TaRegistry.Web.Services;

public sealed record UploadResult(bool Ok, string Message, int? PackageId, IReadOnlyList<string> Warnings);

public sealed class PackageEdit
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public int? Revision { get; set; }
    public string? Summary { get; set; }
    public string? Author { get; set; }
    public string? Publisher { get; set; }
    public string? Homepage { get; set; }
    public string? Notes { get; set; }
    public string? PublishedDay { get; set; }
    public string? Tags { get; set; }
    public string? DetailsJson { get; set; }
}

public sealed class PackageAdminService(RegistryDb db, FileBlobStore blobs)
{
    public async Task<UploadResult> UploadAsync(Stream package, string fileName, Stream? badge, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (!PackageKinds.TryFromExtension(extension, out var kind))
            return new UploadResult(false, "Upload a .oamod, .oamap, .oalang, .oaexp, or .oamut file.", null, []);

        string sha;
        try
        {
            sha = await blobs.SavePackageAsync(package, PackageKinds.Extension(kind), cancellationToken);
        }
        catch (Exception ex)
        {
            return new UploadResult(false, ex.Message, null, []);
        }

        var path = blobs.PackagePath(sha, kind);
        PackageInspection inspection;
        try
        {
            inspection = PackageInspector.Inspect(path);
        }
        catch (PackageFormatException ex)
        {
            if (!await db.Packages.AnyAsync(p => p.Sha256 == sha, cancellationToken))
                blobs.DeleteIfUnreferenced(path);
            return new UploadResult(false, ex.Message, null, []);
        }

        if (inspection.Kind != kind)
            return new UploadResult(false, "The archive kind does not match the file extension.", null, []);

        string? badgeSha = null;
        if (badge is not null)
        {
            try
            {
                badgeSha = await blobs.SavePictureAsync(badge, cancellationToken);
            }
            catch (Exception ex)
            {
                return new UploadResult(false, ex.Message, null, inspection.Warnings);
            }
        }

        var detailsJson = inspection.DetailsJson;
        var warnings = inspection.Warnings.ToList();
        if (inspection.MapPreviews.Count > 0)
            detailsJson = await AttachMapPreviewsAsync(path, detailsJson, inspection.MapPreviews, warnings, cancellationToken);
        if (badgeSha is null && inspection.IconPath is not null)
            badgeSha = await AttachIconAsync(path, inspection.IconPath, warnings, cancellationToken);

        var existing = await db.Packages.SingleOrDefaultAsync(p => p.Kind == inspection.Kind && p.PackageId == inspection.PackageId, cancellationToken);
        var taken = await db.Packages
            .Where(p => p.Kind != inspection.Kind)
            .Select(p => p.PackageId)
            .ToListAsync(cancellationToken);
        if (taken.Any(id => string.Equals(id, inspection.PackageId, StringComparison.OrdinalIgnoreCase)))
        {
            if (!await db.Packages.AnyAsync(p => p.Sha256 == sha, cancellationToken))
                blobs.DeleteIfUnreferenced(path);
            if (badgeSha is not null)
                await DeletePictureIfUnusedAsync(badgeSha, cancellationToken);
            return new UploadResult(false, $"Package id '{inspection.PackageId}' is already used by another kind.", null, warnings);
        }
        var release = existing is null ? 1 : existing.Sha256 == inspection.Sha256 ? existing.Release : existing.Release + 1;
        var previousSha = existing?.Sha256;
        var previousBadge = existing?.BadgeSha;
        var entity = existing ?? new PackageEntity { CreatedAt = DateTime.UtcNow };
        entity.Kind = inspection.Kind;
        entity.PackageId = inspection.PackageId;
        entity.Name = inspection.Name;
        entity.Release = release;
        entity.Size = inspection.Size;
        entity.Sha256 = inspection.Sha256;
        entity.Version = inspection.Version;
        entity.Revision = inspection.Revision;
        entity.Summary = inspection.Summary;
        entity.Author = inspection.Author;
        entity.Homepage = inspection.Homepage;
        entity.TagsJson = inspection.Tags.Count == 0 ? null : JsonSerializer.Serialize(inspection.Tags);
        entity.DetailsJson = detailsJson;
        entity.InCatalogue = false;
        if (badgeSha is not null)
            entity.BadgeSha = badgeSha;
        if (existing is null)
        {
            entity.PublishedDay = UtcTimes.Day(DateTimeOffset.UtcNow);
            db.Packages.Add(entity);
        }

        var problems = CatalogueRules.Problems(PackageMaps.ToDraft(entity));
        if (problems.Any(p => p.Contains("id does not match", StringComparison.Ordinal) || p.Contains("name must be", StringComparison.Ordinal)))
        {
            if (existing is null && !await db.Packages.AnyAsync(p => p.Sha256 == sha, cancellationToken))
                blobs.DeleteIfUnreferenced(path);
            return new UploadResult(false, string.Join(" ", problems), null, warnings);
        }

        await MarkDirtyAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await DeleteBlobIfUnusedAsync(previousSha, entity.Kind, entity.Sha256, cancellationToken);
        if (badgeSha is not null && previousBadge is not null && previousBadge != badgeSha)
            await DeletePictureIfUnusedAsync(previousBadge, cancellationToken);

        return new UploadResult(true, "Package stored. Publish the catalogue before the game can see it.", entity.Id, warnings);
    }

    private async Task<string?> AttachMapPreviewsAsync(
        string packagePath,
        string? detailsJson,
        IReadOnlyList<MapPreview> previews,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (detailsJson is null || JsonNode.Parse(detailsJson) is not JsonObject details || details["maps"] is not JsonArray maps)
            return detailsJson;

        using var zip = ZipFile.OpenRead(packagePath);
        foreach (var preview in previews)
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == preview.ArchivePath);
            if (entry is null || preview.MapIndex >= maps.Count || maps[preview.MapIndex] is not JsonObject map)
            {
                warnings.Add($"Preview {preview.ArchivePath} is not in the archive.");
                continue;
            }

            try
            {
                await using var stream = entry.Open();
                var pictureSha = await blobs.SavePictureAsync(stream, cancellationToken);
                map["preview"] = "/v1/i/" + pictureSha + ".png";
            }
            catch (InvalidOperationException ex)
            {
                warnings.Add($"Preview {preview.ArchivePath}: {ex.Message}");
            }
        }

        return details.ToJsonString();
    }

    private async Task<string?> AttachIconAsync(string packagePath, string iconPath, List<string> warnings, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(packagePath);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == iconPath);
        if (entry is null)
        {
            warnings.Add($"Icon {iconPath} is not in the archive.");
            return null;
        }

        try
        {
            await using var stream = entry.Open();
            return await blobs.SavePictureAsync(stream, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            warnings.Add($"Icon {iconPath}: {ex.Message}");
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> UpdateAsync(PackageEntity entity, PackageEdit edit, CancellationToken cancellationToken)
    {
        entity.Name = edit.Name.Trim();
        entity.Version = Blank(edit.Version);
        entity.Revision = edit.Revision;
        entity.Summary = edit.Summary;
        entity.Author = Blank(edit.Author);
        entity.Publisher = Blank(edit.Publisher);
        entity.Homepage = Blank(edit.Homepage);
        entity.Notes = edit.Notes;
        entity.PublishedDay = Blank(edit.PublishedDay);
        entity.TagsJson = ParseTags(edit.Tags);
        entity.DetailsJson = NormalizeDetails(edit.DetailsJson);
        entity.InCatalogue = false;

        var problems = CatalogueRules.Problems(PackageMaps.ToDraft(entity)).ToList();
        if (problems.Count > 0)
            return problems;

        await MarkDirtyAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return [];
    }

    public async Task DeleteAsync(PackageEntity entity, CancellationToken cancellationToken)
    {
        var sha = entity.Sha256;
        var kind = entity.Kind;
        var badge = entity.BadgeSha;
        db.Packages.Remove(entity);
        await MarkDirtyAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await DeleteBlobIfUnusedAsync(sha, kind, null, cancellationToken);
        if (badge is not null)
            await DeletePictureIfUnusedAsync(badge, cancellationToken);
    }

    private async Task DeleteBlobIfUnusedAsync(string? sha, string kind, string? keep, CancellationToken cancellationToken)
    {
        if (sha is null || sha == keep)
            return;
        if (await db.Packages.AnyAsync(p => p.Sha256 == sha, cancellationToken))
            return;
        blobs.DeleteIfUnreferenced(blobs.PackagePath(sha, kind));
    }

    private async Task DeletePictureIfUnusedAsync(string sha, CancellationToken cancellationToken)
    {
        if (await db.Packages.AnyAsync(p => p.BadgeSha == sha, cancellationToken))
            return;
        blobs.DeleteIfUnreferenced(blobs.PicturePath(sha));
    }

    private async Task MarkDirtyAsync(CancellationToken cancellationToken)
    {
        var state = await db.CatalogueStates.SingleOrDefaultAsync(cancellationToken);
        if (state is not null)
            state.Dirty = true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ParseTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var tags = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return tags.Count == 0 ? null : JsonSerializer.Serialize(tags);
    }

    private static string? NormalizeDetails(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Extra details must be a JSON object.");

        return doc.RootElement.GetRawText();
    }
}
