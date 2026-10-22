namespace TaRegistry.Web.Storage;

public sealed class StorageLayout
{
    public StorageLayout(IWebHostEnvironment environment, Microsoft.Extensions.Options.IOptions<StorageOptions> options)
    {
        Root = Resolve(environment.ContentRootPath, options.Value.Root);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Packages);
        Directory.CreateDirectory(Pictures);
        Directory.CreateDirectory(Keys);
        Directory.CreateDirectory(Temp);
    }

    public string Root { get; }
    public string Packages => Path.Combine(Root, "v1", "p");
    public string Pictures => Path.Combine(Root, "v1", "i");
    public string Keys => Path.Combine(Root, "keys");
    public string Temp => Path.Combine(Root, "tmp");
    public string KeyPath(string keyId) => Path.Combine(Keys, keyId + ".key");
    public string Database => Path.Combine(Root, "registry.db");
    public string DescriptorFile => Path.Combine(Root, "registry.yaml");

    public static string Resolve(string contentRoot, string configured)
    {
        if (Path.IsPathRooted(configured))
            return Path.GetFullPath(configured);

        return Path.GetFullPath(Path.Combine(contentRoot, configured));
    }
}
