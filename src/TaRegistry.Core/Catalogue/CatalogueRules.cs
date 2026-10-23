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

        if (package.Kind == PackageKinds.Mutator)
        {
            if (package.Version is null || !MutatorVersionOk(package.Version))
                problems.Add($"{label}: version must be one to three dotted parts, each 0 to 65535 with no leading zero, at most 32 bytes.");
        }
        else if (package.Kind is PackageKinds.Mod or PackageKinds.Expansion)
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

        if (package.Kind == PackageKinds.Mutator)
            CheckMutator(problems, package, hasDetails ? details : default);

        return problems;
    }

    public static IReadOnlyList<string> DuplicateIds(IEnumerable<CataloguePackage> packages)
    {
        var problems = new List<string>();
        foreach (var group in packages.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Package id '{group.First().Id}' is used by more than one package. A catalogue key is unique across kinds.");

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

    private static readonly HashSet<string> LuaReserved = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto",
        "if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while"
    };

    private static readonly HashSet<string> LayerNames = new(StringComparer.Ordinal)
    {
        "files", "units", "weapons", "features", "sides", "tables", "menus", "maps", "hacks"
    };

    private static void CheckMutator(List<string> problems, CataloguePackage package, JsonElement details)
    {
        if (details.ValueKind != JsonValueKind.Object)
            return;

        var label = $"{package.Kind} {package.Id}";
        var prerequisites = new HashSet<string>(StringComparer.Ordinal);
        if (details.TryGetProperty("requires", out var requires) && requires.ValueKind == JsonValueKind.Object
            && requires.TryGetProperty("mutators", out var mutators))
            CheckConditions(problems, label, "requires.mutators", mutators, 16, package.Id, prerequisites, null);

        if (details.TryGetProperty("conflicts_with", out var conflicts))
            CheckConditions(problems, label, "conflicts_with", conflicts, 32, package.Id, [], prerequisites);

        if (details.TryGetProperty("works_with", out var works))
            CheckWorksWith(problems, label, works);

        if (details.TryGetProperty("layers", out var layers))
            CheckLayers(problems, label, layers);

        if (details.TryGetProperty("clashes", out var clashes)
            && (clashes.ValueKind != JsonValueKind.String || clashes.GetString() is not ("add" or "replace")))
            problems.Add($"{label}: clashes must be add or replace.");

        if (details.TryGetProperty("options", out var options))
            CheckOptions(problems, label, options);
    }

    private static void CheckConditions(
        List<string> problems,
        string label,
        string field,
        JsonElement node,
        int max,
        string packageId,
        HashSet<string> seen,
        HashSet<string>? forbidden)
    {
        if (node.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{label}: {field} must be an array.");
            return;
        }

        if (node.GetArrayLength() > max)
            problems.Add($"{label}: {field} has more than {max} entries.");

        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var idEl)
                || idEl.ValueKind != JsonValueKind.String
                || idEl.GetString() is not string id
                || !KebabPattern().IsMatch(id)
                || TextRules.Utf8Length(id) is < 1 or > 64)
            {
                problems.Add($"{label}: {field} entries need an id.");
                continue;
            }

            if (string.Equals(id, packageId, StringComparison.Ordinal))
                problems.Add($"{label}: {field} cannot name this package.");
            else if (forbidden is not null && forbidden.Contains(id))
                problems.Add($"{label}: {field} '{id}' is already a prerequisite.");
            else if (!seen.Add(id))
                problems.Add($"{label}: {field} repeats '{id}'.");

            if (item.TryGetProperty("version", out var versionEl))
            {
                if (versionEl.ValueKind != JsonValueKind.String
                    || versionEl.GetString() is not string version
                    || TextRules.Utf8Length(version) is < 1 or > 64)
                    problems.Add($"{label}: {field} version for '{id}' must be 1 to 64 bytes.");
            }
        }
    }

    private static void CheckWorksWith(List<string> problems, string label, JsonElement works)
    {
        if (works.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{label}: works_with must be an array.");
            return;
        }

        if (works.GetArrayLength() > 32)
            problems.Add($"{label}: works_with has more than 32 entries.");

        var star = false;
        var count = 0;
        foreach (var item in works.EnumerateArray())
        {
            count++;
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not string text || TextRules.Utf8Length(text) is < 1 or > 64)
            {
                problems.Add($"{label}: works_with entries must be 1 to 64 bytes.");
                continue;
            }

            if (text == "*")
                star = true;
        }

        if (star && count != 1)
            problems.Add($"{label}: '*' in works_with only stands alone.");
    }

    private static void CheckLayers(List<string> problems, string label, JsonElement layers)
    {
        if (layers.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{label}: layers must be an array.");
            return;
        }

        if (layers.GetArrayLength() > 16)
            problems.Add($"{label}: layers has more than 16 entries.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in layers.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not string name || !LayerNames.Contains(name))
                problems.Add($"{label}: layers only names files, units, weapons, features, sides, tables, menus, maps, or hacks.");
            else if (!seen.Add(name))
                problems.Add($"{label}: layers repeats '{name}'.");
        }
    }

    private static void CheckOptions(List<string> problems, string label, JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{label}: options must be an array.");
            return;
        }

        if (options.GetArrayLength() > 16)
            problems.Add($"{label}: options has more than 16 entries.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in options.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{label}: each option must be an object.");
                continue;
            }

            if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String || idEl.GetString() is not string id)
            {
                problems.Add($"{label}: each option needs an id.");
                continue;
            }

            if (!OptionIdPattern().IsMatch(id) || TextRules.Utf8Length(id) > 32 || LuaReserved.Contains(id))
                problems.Add($"{label}: option id '{id}' is not a Lua name.");
            else if (!seen.Add(id))
                problems.Add($"{label}: option id '{id}' is repeated.");

            if (!item.TryGetProperty("label", out var labelEl) || labelEl.ValueKind != JsonValueKind.String
                || labelEl.GetString() is not string optionLabel || TextRules.Utf8Length(optionLabel) is < 1 or > 64)
                problems.Add($"{label}: option '{id}' needs a label of 1 to 64 bytes.");

            var type = item.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString()
                : null;
            if (type is not ("toggle" or "choice" or "number"))
                continue;

            if (type == "choice")
                CheckChoice(problems, label, id, item);
            else if (type == "number")
                CheckNumber(problems, label, id, item);
            else if (item.TryGetProperty("default", out var toggleDefault) && toggleDefault.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                problems.Add($"{label}: option '{id}' default must be true or false.");
        }
    }

    private static void CheckChoice(List<string> problems, string label, string id, JsonElement option)
    {
        if (!option.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() is < 2 or > 16)
        {
            problems.Add($"{label}: option '{id}' needs 2 to 16 values.");
            return;
        }

        bool? numbers = null;
        foreach (var value in values.EnumerateArray())
        {
            var isNumber = value.ValueKind == JsonValueKind.Number;
            var isName = value.ValueKind == JsonValueKind.String && value.GetString() is string text && OptionValuePattern().IsMatch(text);
            if (isNumber == isName)
            {
                problems.Add($"{label}: option '{id}' values must be all numbers or all names.");
                return;
            }

            numbers ??= isNumber;
            if (numbers != isNumber)
            {
                problems.Add($"{label}: option '{id}' values must be all numbers or all names.");
                return;
            }
        }

        if (option.TryGetProperty("labels", out var labels))
        {
            if (labels.ValueKind != JsonValueKind.Array || labels.GetArrayLength() != values.GetArrayLength())
                problems.Add($"{label}: option '{id}' labels must match its values.");
            else
            {
                foreach (var text in labels.EnumerateArray())
                {
                    if (text.ValueKind != JsonValueKind.String || text.GetString() is not string labelText || TextRules.Utf8Length(labelText) is < 1 or > 64)
                        problems.Add($"{label}: option '{id}' labels must be 1 to 64 bytes.");
                }
            }
        }
    }

    private static void CheckNumber(List<string> problems, string label, string id, JsonElement option)
    {
        if (!option.TryGetProperty("min", out var minEl) || minEl.ValueKind != JsonValueKind.Number
            || !option.TryGetProperty("max", out var maxEl) || maxEl.ValueKind != JsonValueKind.Number
            || !minEl.TryGetDouble(out var min) || !maxEl.TryGetDouble(out var max) || min >= max)
            problems.Add($"{label}: option '{id}' needs min less than max.");

        if (option.TryGetProperty("step", out var stepEl)
            && (stepEl.ValueKind != JsonValueKind.Number || !stepEl.TryGetDouble(out var step) || step <= 0))
            problems.Add($"{label}: option '{id}' step must be greater than 0.");
    }

    private static bool MutatorVersionOk(string version)
    {
        if (TextRules.Utf8Length(version) is < 1 or > 32 || !MutatorVersionPattern().IsMatch(version))
            return false;

        foreach (var part in version.Split('.'))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > 65535)
                return false;
        }

        return true;
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

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex OptionIdPattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex OptionValuePattern();

    [GeneratedRegex("^(0|[1-9][0-9]{0,4})(\\.(0|[1-9][0-9]{0,4})){0,2}$")]
    private static partial Regex MutatorVersionPattern();

    [GeneratedRegex("^(?i:https?)://\\S+$")]
    private static partial Regex HomepagePattern();
}
