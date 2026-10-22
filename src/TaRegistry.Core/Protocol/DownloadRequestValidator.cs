using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaRegistry.Core.Protocol;

public sealed record DownloadRequest(
    string? Install,
    string Package,
    string Kind,
    int Release,
    string Sha256,
    string Engine,
    string Platform,
    string Arch,
    string Language,
    string Reason);

public abstract record DownloadValidation
{
    public sealed record Ok(DownloadRequest Request) : DownloadValidation;
    public sealed record Invalid(string Field) : DownloadValidation;
    public sealed record NotObject : DownloadValidation;
}

public static partial class DownloadRequestValidator
{
    public static DownloadValidation Validate(JsonElement body, bool installRequired)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return new DownloadValidation.NotObject();

        if (!TryString(body, "install", out var install, out var installPresent))
            return new DownloadValidation.Invalid("install");

        if (installRequired && !installPresent)
            return new DownloadValidation.Invalid("install");

        if (installPresent && (install is null || !InstallPattern().IsMatch(install)))
            return new DownloadValidation.Invalid("install");

        if (!TryString(body, "package", out var package, out var packagePresent) || !packagePresent || package is null || !PackagePattern().IsMatch(package))
            return new DownloadValidation.Invalid("package");

        if (!TryString(body, "kind", out var kind, out var kindPresent) || !kindPresent || kind is null || !PackageKinds.IsKnown(kind))
            return new DownloadValidation.Invalid("kind");

        if (!TryWhole(body, "release", out var release, out var releasePresent) || !releasePresent || release is < 1 or > 2147483647)
            return new DownloadValidation.Invalid("release");

        if (!TryString(body, "sha256", out var sha, out var shaPresent) || !shaPresent || sha is null || !ShaPattern().IsMatch(sha))
            return new DownloadValidation.Invalid("sha256");

        if (!TryString(body, "engine", out var engine, out var enginePresent) || !enginePresent || engine is null || !EnginePattern().IsMatch(engine))
            return new DownloadValidation.Invalid("engine");

        if (!TryString(body, "platform", out var platform, out var platformPresent) || !platformPresent || platform is null || !PlatformPattern().IsMatch(platform))
            return new DownloadValidation.Invalid("platform");

        if (!TryString(body, "arch", out var arch, out var archPresent) || !archPresent || arch is null || !ArchPattern().IsMatch(arch))
            return new DownloadValidation.Invalid("arch");

        if (!TryString(body, "language", out var language, out var languagePresent))
            return new DownloadValidation.Invalid("language");

        language ??= "";
        if (languagePresent && language is null)
            return new DownloadValidation.Invalid("language");

        if (!LanguagePattern().IsMatch(language))
            return new DownloadValidation.Invalid("language");

        if (!TryString(body, "reason", out var reason, out var reasonPresent) || !reasonPresent || reason is null || reason is not ("install" or "update" or "repair" or "offered-in-lobby" or "mirror"))
            return new DownloadValidation.Invalid("reason");

        return new DownloadValidation.Ok(new DownloadRequest(
            installPresent ? install : null,
            package,
            kind,
            (int)release,
            sha.ToLowerInvariant(),
            engine,
            platform,
            arch,
            language,
            reason));
    }

    private static bool TryString(JsonElement body, string name, out string? value, out bool present)
    {
        value = null;
        present = false;
        if (!body.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;

        present = true;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        value = element.GetString();
        return true;
    }

    private static bool TryWhole(JsonElement body, string name, out long value, out bool present)
    {
        value = 0;
        present = false;
        if (!body.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;

        present = true;
        if (element.ValueKind != JsonValueKind.Number)
            return false;

        var raw = element.GetRawText();
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return false;

        return true;
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex InstallPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex PackagePattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex ShaPattern();

    [GeneratedRegex("^[0-9A-Za-z.+-]{1,32}$")]
    private static partial Regex EnginePattern();

    [GeneratedRegex("^[a-z0-9-]{1,16}$")]
    private static partial Regex PlatformPattern();

    [GeneratedRegex("^[a-z0-9_]{1,16}$")]
    private static partial Regex ArchPattern();

    [GeneratedRegex("^$|^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8}){0,3}$")]
    private static partial Regex LanguagePattern();
}
