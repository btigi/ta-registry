namespace TaRegistry.Core;

public static class PackageKinds
{
    public const string Mod = "oamod";
    public const string Map = "oamap";
    public const string Language = "oalang";
    public const string Expansion = "expansion";

    public static readonly string[] All = [Mod, Map, Language, Expansion];

    public static bool IsKnown(string kind) =>
        kind is Mod or Map or Language or Expansion;

    public static string Extension(string kind) =>
        kind == Expansion ? "oaexp" : kind;

    public static bool TryFromExtension(string extension, out string kind)
    {
        switch (extension.TrimStart('.').ToLowerInvariant())
        {
            case "oamod":
                kind = Mod;
                return true;
            case "oamap":
                kind = Map;
                return true;
            case "oalang":
                kind = Language;
                return true;
            case "oaexp":
                kind = Expansion;
                return true;
            default:
                kind = "";
                return false;
        }
    }

    public static string ManifestName(string kind) => kind switch
    {
        Mod => "oamod.yaml",
        Map => "oamap.yaml",
        Language => "language.yaml",
        Expansion => "oaexp.yaml",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string FileName(string sha256, string kind) =>
        $"{sha256}.{Extension(kind)}";
}
