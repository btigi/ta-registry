using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace TaRegistry.Core.Packages;

public sealed class PackageInspection
{
    public required string Kind { get; init; }
    public required string Extension { get; init; }
    public required string Sha256 { get; init; }
    public required long Size { get; init; }
    public required string PackageId { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public int? Revision { get; init; }
    public string? Summary { get; init; }
    public string? Author { get; init; }
    public string? Homepage { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? DetailsJson { get; init; }
    public IReadOnlyList<MapPreview> MapPreviews { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A map preview inside the archive, for the map at <paramref name="MapIndex"/> of the catalogue's maps.</summary>
public sealed record MapPreview(int MapIndex, string ArchivePath);

public sealed class PackageFormatException : Exception
{
    public PackageFormatException(string message) : base(message)
    {
    }
}

public static class PackageInspector
{
    public static PackageInspection Inspect(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (!PackageKinds.TryFromExtension(extension, out var kind))
            throw new PackageFormatException($"Unsupported package extension '.{extension}'.");

        var file = new FileInfo(path);
        if (file.Length is < 1 or > 4294967296)
            throw new PackageFormatException("Package size must be from 1 byte to 4 GiB.");

        string sha;
        using (var stream = File.OpenRead(path))
            sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        using var zip = ZipFile.OpenRead(path);
        RejectUnsafeEntries(zip);
        var manifestName = PackageKinds.ManifestName(kind);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == manifestName);
        if (entry is null)
            throw new PackageFormatException($"The archive must contain {manifestName} at its root.");

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var yaml = reader.ReadToEnd();
        if (Encoding.UTF8.GetByteCount(yaml) > 256 * 1024)
            throw new PackageFormatException("The manifest is larger than 256 KiB.");

        return Parse(kind, extension, sha, file.Length, yaml);
    }

    private static void RejectUnsafeEntries(ZipArchive zip)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains("..", StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal))
                throw new PackageFormatException($"Archive path '{entry.FullName}' is not safe.");

            if (!seen.Add(name))
                throw new PackageFormatException($"Archive paths differ only by case: '{entry.FullName}'.");
        }
    }

    private static PackageInspection Parse(string kind, string extension, string sha, long size, string yaml)
    {
        JsonNode? manifest;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            manifest = stream.Documents.Count == 0 ? null : YamlNodes.ToNode(stream.Documents[0].RootNode);
        }
        catch (Exception ex) when (ex is not PackageFormatException)
        {
            throw new PackageFormatException("The manifest is not YAML: " + ex.Message);
        }

        if (manifest is not JsonObject root)
            throw new PackageFormatException("The manifest must be a YAML mapping.");

        var id = Scalar(root, "id");
        var name = Scalar(root, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            throw new PackageFormatException("The manifest needs id and name.");

        var warnings = new List<string>();
        int? revision = null;
        if (root.TryGetPropertyValue("revision", out var revisionNode) && revisionNode is JsonValue revisionValue)
        {
            if (revisionValue.TryGetValue<long>(out var whole) && whole is >= 1 and <= 65535)
                revision = (int)whole;
            else if (revisionValue.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) && parsed is >= 1 and <= 65535)
                revision = parsed;
            else
                warnings.Add("revision was present but not a whole number from 1 to 65535.");
        }

        var tags = new List<string>();
        if (root.TryGetPropertyValue("tags", out var tagsNode) && tagsNode is JsonArray tagArray)
        {
            foreach (var tag in tagArray)
            {
                if (tag is JsonValue tagValue && tagValue.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                    tags.Add(text.Trim());
            }
        }

        var details = new JsonObject();
        var mapPreviews = new List<MapPreview>();
        Copy(root, details, "requires", "sim_hash", "hacks", "coverage", "english_name", "word", "locales", "fallbacks", "needs", "extends");
        if (kind == PackageKinds.Map && root["maps"] is JsonArray manifestMaps)
            details["maps"] = CatalogueMaps(manifestMaps, id.Trim(), mapPreviews);
        else
            Copy(root, details, "maps");

        var author = Scalar(root, "author");
        if (author is null && root["author"] is JsonObject authorObject)
            author = Scalar(authorObject, "name");

        return new PackageInspection
        {
            Kind = kind,
            Extension = extension,
            Sha256 = sha,
            Size = size,
            PackageId = id.Trim(),
            Name = name.Trim(),
            Version = Scalar(root, "version"),
            Revision = revision,
            Summary = Scalar(root, "summary") ?? Scalar(root, "description"),
            Author = author,
            Homepage = Scalar(root, "homepage"),
            Tags = tags,
            DetailsJson = details.Count == 0 ? null : details.ToJsonString(),
            MapPreviews = mapPreviews,
            Warnings = warnings
        };
    }

    private static JsonArray CatalogueMaps(JsonArray manifestMaps, string packageId, List<MapPreview> previews)
    {
        var maps = new JsonArray();
        foreach (var node in manifestMaps)
        {
            if (node is not JsonObject entry)
                continue;

            var map = new JsonObject();
            var stem = Scalar(entry, "stem");
            var mapId = Scalar(entry, "map");
            if (mapId is null && stem is not null)
                mapId = stem + "@" + packageId;
            if (mapId is not null)
                map["map"] = mapId;

            Copy(entry, map, "name", "players", "size", "compatible");
            if (Scalar(entry, "preview") is string preview && preview.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                previews.Add(new MapPreview(maps.Count, preview.Replace('\\', '/')));

            maps.Add(map);
        }

        return maps;
    }

    private static void Copy(JsonObject source, JsonObject target, params string[] names)
    {
        foreach (var name in names)
        {
            if (source.TryGetPropertyValue(name, out var node) && node is not null)
                target[name] = node.DeepClone();
        }
    }

    private static string? Scalar(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
            return null;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
                return text;
            return value.ToJsonString().Trim('"');
        }

        return null;
    }
}

internal static class YamlNodes
{
    public static JsonNode? ToNode(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
            {
                var obj = new JsonObject();
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is YamlScalarNode { Value: { Length: > 0 } name })
                        obj[name] = ToNode(value);
                }

                return obj;
            }
            case YamlSequenceNode sequence:
            {
                var array = new JsonArray();
                foreach (var item in sequence.Children)
                    array.Add(ToNode(item));
                return array;
            }
            case YamlScalarNode scalar:
                return Scalar(scalar);
            default:
                return null;
        }
    }

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? "";
        if (scalar.Style != ScalarStyle.Plain)
            return JsonValue.Create(text);

        if (text is "" or "~" or "null" or "Null" or "NULL")
            return null;
        if (text is "true" or "True" or "TRUE")
            return JsonValue.Create(true);
        if (text is "false" or "False" or "FALSE")
            return JsonValue.Create(false);
        if (long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var whole))
            return JsonValue.Create(whole);
        if (text.Contains('.') && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var real))
            return JsonValue.Create(real);

        return JsonValue.Create(text);
    }
}
