using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaRegistry.Core.Catalogue;

public static partial class CatalogueRules
{
    public static IReadOnlyList<string> Problems(CataloguePackage package)
    {
        var problems = new List<string>();
        var label = $"{package.Kind} {package.Id}";

        if (!ValidId(package.Kind, package.Id))
            problems.Add($"{label}: id does not match the catalogue rule for this kind.");

        if (TextRules.Utf8Length(package.Name) is < 1 or > 64)
            problems.Add($"{label}: name must be 1 to 64 bytes.");

        if (package.Release is < 1 or > 2147483647)
            problems.Add($"{label}: release is out of range.");

        if (package.Size is < 1 or > 4294967296)
            problems.Add($"{label}: size is out of range.");

        if (!ShaPattern().IsMatch(package.Sha256))
            problems.Add($"{label}: sha256 must be 64 lower-case hex digits.");

        if (package.Kind is PackageKinds.Mod or PackageKinds.Expansion)
        {
            if (package.Version is null || TextRules.Utf8Length(package.Version) is < 1 or > 32)
                problems.Add($"{label}: version is required and must be 1 to 32 bytes.");
        }
        else if (package.Version is not null && TextRules.Utf8Length(package.Version) is < 1 or > 32)
        {
            problems.Add($"{label}: version must be 1 to 32 bytes.");
        }

        if (package.Kind == PackageKinds.Mod)
        {
            if (package.Revision is null or < 1 or > 65535)
                problems.Add($"{label}: revision is required and must be from 1 to 65535.");
        }
        else if (package.Revision is < 1 or > 65535)
        {
            problems.Add($"{label}: revision must be from 1 to 65535.");
        }

        CheckOptional(problems, label, "publisher", package.Publisher, 64);
        CheckOptional(problems, label, "author", package.Author, 64);
        if (package.Summary is not null && TextRules.Utf8Length(package.Summary) > 200)
            problems.Add($"{label}: summary is longer than 200 bytes.");
        if (package.Notes is not null && TextRules.Utf8Length(package.Notes) > 2000)
            problems.Add($"{label}: notes are longer than 2000 bytes.");
        if (package.Homepage is not null && (TextRules.Utf8Length(package.Homepage) > 256 || !HomepagePattern().IsMatch(package.Homepage)))
            problems.Add($"{label}: homepage must be an http or https address.");
        if (package.Published is not null && !UtcTimes.IsRealDay(package.Published))
            problems.Add($"{label}: published must be a real YYYY-MM-DD day.");
        if (package.Badge is not null && !package.Badge.EndsWith(".png", StringComparison.Ordinal))
            problems.Add($"{label}: badge must be a picture reference ending in .png.");

        if (package.Tags is not null)
        {
            if (package.Tags.Count > 16)
                problems.Add($"{label}: at most 16 tags are allowed.");
            foreach (var tag in package.Tags)
            {
                if (TextRules.Utf8Length(tag) is < 1 or > 32 || !TagPattern().IsMatch(tag))
                    problems.Add($"{label}: tag '{tag}' is not lower-case kebab-case of 1 to 32 bytes.");
            }
        }

        JsonElement details = default;
        var hasDetails = false;
        if (!string.IsNullOrWhiteSpace(package.DetailsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(package.DetailsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    problems.Add($"{label}: extra details must be a JSON object.");
                    return problems;
                }

                details = doc.RootElement.Clone();
                hasDetails = true;
            }
            catch (JsonException)
            {
                problems.Add($"{label}: extra details are not JSON.");
                return problems;
            }
        }

        if (package.Kind == PackageKinds.Map)
            CheckMaps(problems, package, hasDetails ? details : default);

        if (package.Kind == PackageKinds.Expansion)
            CheckExtends(problems, label, hasDetails ? details : default);

        return problems;
    }

    public static IReadOnlyList<string> DescriptorProblems(DescriptorModel model)
    {
        var problems = new List<string>();
        if (!RegistryIdPattern().IsMatch(model.Id) || TextRules.Utf8Length(model.Id) > 32)
            problems.Add("Registry id must be kebab-case, 1 to 32 bytes.");

        if (TextRules.Utf8Length(model.Name) is < 1 or > 64 || TextRules.HasControl(model.Name))
            problems.Add("Registry name must be 1 to 64 bytes with no control characters.");

        if (model.Title is not null && (model.Title.Length is < 1 or > 96 || TextRules.HasControl(model.Title)))
            problems.Add("Title must be 1 to 96 characters.");

        if (model.Description is not null && (model.Description.Length is < 1 or > 400 || TextRules.HasControl(model.Description)))
            problems.Add("Description must be 1 to 400 characters.");

        if (model.Author is not null && (TextRules.Utf8Length(model.Author) is < 1 or > 64 || TextRules.HasControl(model.Author)))
            problems.Add("Author must be 1 to 64 bytes with no control characters.");

        if (model.Homepage is not null && !TextRules.IsHttpUrl(model.Homepage, allowQuery: true))
            problems.Add("Homepage must be an http or https address.");

        if (!TextRules.IsHttpUrl(model.CatalogueUrl, allowQuery: false))
            problems.Add("Catalogue address must be http or https with no query.");

        if (!TextRules.IsHttpUrl(model.ApiUrl, allowQuery: false))
            problems.Add("Download API address must be http or https with no query.");

        if (model.Keys.Count > 8)
            problems.Add("A descriptor has at most 8 signing keys.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var publics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in model.Keys)
        {
            if (!ids.Add(key.Id))
                problems.Add($"Signing key id '{key.Id}' is repeated.");
            if (!publics.Add(key.Public))
                problems.Add("Two signing keys share the same public key.");
        }

        return problems;
    }

    private static void CheckOptional(List<string> problems, string label, string field, string? value, int max)
    {
        if (value is null)
            return;

        var length = TextRules.Utf8Length(value);
        if (length < 1 || length > max)
            problems.Add($"{label}: {field} must be 1 to {max} bytes.");
    }

    private static void CheckMaps(List<string> problems, CataloguePackage package, JsonElement details)
    {
        if (details.ValueKind != JsonValueKind.Object || !details.TryGetProperty("maps", out var maps) || maps.ValueKind != JsonValueKind.Array || maps.GetArrayLength() == 0)
        {
            problems.Add($"{package.Kind} {package.Id}: a map pack needs a non-empty maps array.");
            return;
        }

        var index = 0;
        foreach (var map in maps.EnumerateArray())
        {
            index++;
            if (map.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{package.Id}: map {index} is not an object.");
                continue;
            }

            if (!map.TryGetProperty("map", out var idEl) || idEl.ValueKind != JsonValueKind.String || idEl.GetString() is not string mapId || !MapIdOk(mapId, package.Id))
                problems.Add($"{package.Id}: map {index} needs map 'stem@{package.Id}'.");

            if (!map.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String || nameEl.GetString() is not string mapName || TextRules.Utf8Length(mapName) is < 1 or > 64)
                problems.Add($"{package.Id}: map {index} needs a name of 1 to 64 bytes.");

            if (!map.TryGetProperty("players", out var playersEl) || playersEl.ValueKind != JsonValueKind.Number
                || !int.TryParse(playersEl.GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out var players)
                || players is < 1 or > 10)
                problems.Add($"{package.Id}: map {index} needs players from 1 to 10.");

            if (!map.TryGetProperty("size", out var sizeEl) || sizeEl.ValueKind != JsonValueKind.String || sizeEl.GetString() is not string size || !SizeOk(size))
                problems.Add($"{package.Id}: map {index} needs size as widthxheight, each side 1 to 256.");
        }
    }

    private static void CheckExtends(List<string> problems, string label, JsonElement details)
    {
        if (details.ValueKind != JsonValueKind.Object || !details.TryGetProperty("extends", out var extends) || extends.ValueKind != JsonValueKind.Array || extends.GetArrayLength() == 0)
        {
            problems.Add($"{label}: an expansion needs a non-empty extends list.");
            return;
        }

        foreach (var item in extends.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()))
                problems.Add($"{label}: extends entries must be strings.");
        }
    }

    private static bool ValidId(string kind, string id)
    {
        if (id.Contains('@', StringComparison.Ordinal))
            return false;

        if (kind == PackageKinds.Language)
            return TextRules.Utf8Length(id) is >= 2 and <= 35 && LanguageIdPattern().IsMatch(id);

        return TextRules.Utf8Length(id) is >= 1 and <= 64 && KebabPattern().IsMatch(id);
    }

    private static bool MapIdOk(string mapId, string packageId)
    {
        var at = mapId.IndexOf('@');
        if (at <= 0 || at != mapId.LastIndexOf('@'))
            return false;

        return mapId[(at + 1)..] == packageId && mapId[..at].Length > 0;
    }

    private static bool SizeOk(string size)
    {
        var parts = size.Split('x');
        if (parts.Length != 2)
            return false;

        return SideOk(parts[0]) && SideOk(parts[1]);
    }

    private static bool SideOk(string side)
    {
        if (side.Length > 1 && side[0] == '0')
            return false;

        return int.TryParse(side, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 256;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabPattern();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex RegistryIdPattern();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex TagPattern();

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8}){0,3}$")]
    private static partial Regex LanguageIdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex ShaPattern();

    [GeneratedRegex("^(?i:https?)://\\S+$")]
    private static partial Regex HomepagePattern();
}
