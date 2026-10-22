namespace TaRegistry.Web;

public sealed class RegistryOptions
{
    public string Id { get; set; } = "local";
    public string Name { get; set; } = "Local Registry";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? Homepage { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:5080";
    public string InstallId { get; set; } = "required";
    public int CatalogueExpiryDays { get; set; } = 30;
    public int DownloadUrlTtlSeconds { get; set; } = 600;
    public RateLimitOptions RateLimit { get; set; } = new();
}

public sealed class RateLimitOptions
{
    public int KeysPerHour { get; set; } = 30;
    public int ChallengeAfter { get; set; } = 10;
}

public sealed class StorageOptions
{
    public string Root { get; set; } = "../data";
}

public sealed class SigningOptions
{
    public string KeyId { get; set; } = "publisher-2026";
    public string Passphrase { get; set; } = "";
}

public sealed class AdminOptions
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}
