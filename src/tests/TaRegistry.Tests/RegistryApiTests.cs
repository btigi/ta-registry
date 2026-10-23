using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaRegistry.Core.Signing;
using TaRegistry.Web.Data;
using TaRegistry.Web.Services;

namespace TaRegistry.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RegistryCollection : ICollectionFixture<RegistryApp>
{
    public const string Name = "registry";
}

public sealed class RegistryApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ta-registry-" + Guid.NewGuid().ToString("n"));

    public string PackageSha { get; private set; } = "";

    public const string PackageId = "example-mod";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Root"] = _root,
                ["Signing:Passphrase"] = "test-passphrase-value",
                ["Signing:KeyId"] = "test-key",
                ["Registry:Id"] = "testreg",
                ["Registry:Name"] = "Test Registry",
                ["Registry:Title"] = "Test",
                ["Registry:Description"] = "Test registry",
                ["Registry:BaseUrl"] = "http://127.0.0.1:5080",
                ["Registry:InstallId"] = "required",
                ["Registry:RateLimit:KeysPerHour"] = "5",
                ["Registry:RateLimit:ChallengeAfter"] = "3",
                ["Admin:Email"] = "admin@localhost",
                ["Admin:Password"] = "ChangeMe-Admin1!"
            });
        });
    }

    public async Task InitializeAsync()
    {
        using var _ = CreateClient();
        await SeedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var packages = scope.ServiceProvider.GetRequiredService<PackageAdminService>();
        var publisher = scope.ServiceProvider.GetRequiredService<CataloguePublisher>();
        await using var zip = BuildMod();
        var uploaded = await packages.UploadAsync(zip, "example-mod.oamod", null, CancellationToken.None);
        if (!uploaded.Ok)
            throw new InvalidOperationException(uploaded.Message);

        var published = await publisher.PublishAsync(CancellationToken.None);
        if (!published.Ok)
            throw new InvalidOperationException(string.Join("; ", published.Problems));

        await using var read = Services.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<RegistryDb>();
        PackageSha = (await db.Packages.SingleAsync()).Sha256;
    }

    private static MemoryStream BuildMod()
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("oamod.yaml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("""
                oamod: 1
                id: example-mod
                name: Example Mod
                version: "1.0.0"
                revision: 1
                summary: A test mod.
                """);
        }

        stream.Position = 0;
        return stream;
    }
}

[Collection(RegistryCollection.Name)]
public sealed class RegistryApiTests(RegistryApp app)
{
    [Fact]
    public async Task Catalogue_is_signed()
    {
        var client = app.CreateClient();
        var json = await client.GetByteArrayAsync("/v1/catalogue.json");
        var signature = (await client.GetStringAsync("/v1/catalogue.json.sig")).Trim();
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("catalogue").GetInt32());
        Assert.Equal("testreg", document.RootElement.GetProperty("registry").GetString());
        Assert.Contains(RegistryApp.PackageId, document.RootElement.GetProperty("packages").EnumerateArray().Select(p => p.GetProperty("id").GetString()));

        var parts = signature.Split(' ');
        Assert.Equal("ed25519", parts[0]);
        var publicText = document.RootElement.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("id").GetString() == parts[1]).GetProperty("public").GetString()!;
        Assert.True(Ed25519Key.TryParsePublic(publicText, out var publicKey));
        Assert.True(Ed25519Key.Verify(publicKey, json, Convert.FromBase64String(parts[2])));
    }

    [Fact]
    public async Task Download_result_and_reissue_follow_the_spec()
    {
        var client = app.CreateClient();
        var install = Guid.NewGuid().ToString("D");
        var first = await PostDownload(client, install);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var granted = await first.Content.ReadFromJsonAsync<JsonElement>();
        var downloadId = granted.GetProperty("download").GetString()!;
        var tokenPath = new Uri(granted.GetProperty("url").GetString()!).AbsolutePath;
        var file = await client.GetAsync(tokenPath);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);

        var direct = await client.GetAsync("/v1/p/" + app.PackageSha + ".oamod");
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);

        var again = await PostDownload(client, install);
        var second = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(downloadId, second.GetProperty("download").GetString());
        Assert.NotEqual(granted.GetProperty("url").GetString(), second.GetProperty("url").GetString());

        var result = await client.PostAsJsonAsync("/api/v1/downloads/" + downloadId + "/result", new
        {
            result = "installed",
            bytes = 20,
            seconds = 1
        });
        Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/v1/downloads/" + downloadId + "/result", new
        {
            result = "failed",
            bytes = 0,
            seconds = 0
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("result_recorded", await ErrorCode(duplicate));

        var fresh = await PostDownload(client, install);
        Assert.Equal(HttpStatusCode.Created, fresh.StatusCode);
        var third = await fresh.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(downloadId, third.GetProperty("download").GetString());
    }

    [Fact]
    public async Task Unknown_package_and_release_mismatch()
    {
        var client = app.CreateClient();
        var missing = await PostDownload(client, Guid.NewGuid().ToString("D"), package: "missing-mod");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("unknown_package", await ErrorCode(missing));

        var mismatch = await PostDownload(client, Guid.NewGuid().ToString("D"), release: 9);
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        var body = await mismatch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("release_mismatch", body.GetProperty("error").GetString());
        Assert.True(body.GetProperty("current_release").GetInt32() >= 1);
    }

    [Fact]
    public async Task Challenge_then_rate_limit()
    {
        var client = app.CreateClient();
        var install = Guid.NewGuid().ToString("D");
        Assert.Equal(HttpStatusCode.Created, (await PostDownload(client, install)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostDownload(client, install)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostDownload(client, install)).StatusCode);

        var challenged = await PostDownload(client, install);
        Assert.Equal((HttpStatusCode)428, challenged.StatusCode);
        var challenge = await challenged.Content.ReadFromJsonAsync<JsonElement>();
        var code = challenge.GetProperty("challenge").GetProperty("code").GetString()!;

        var pending = await client.GetFromJsonAsync<JsonElement>("/api/v1/challenges/" + code);
        Assert.Equal("pending", pending.GetProperty("state").GetString());

        var solved = await client.PostAsync("/api/v1/challenges/" + code + "/solve", content: null);
        Assert.Equal(HttpStatusCode.OK, solved.StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await PostDownload(client, install)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostDownload(client, install)).StatusCode);

        var limited = await PostDownload(client, install);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", await ErrorCode(limited));
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var retry));
        Assert.NotEmpty(retry!);
    }

    [Fact]
    public async Task Counts_list_the_package()
    {
        var client = app.CreateClient();
        await PostDownload(client, Guid.NewGuid().ToString("D"));
        var counts = await client.GetFromJsonAsync<JsonElement>("/api/v1/counts");
        Assert.Equal(1, counts.GetProperty("counts").GetInt32());
        Assert.Contains(counts.GetProperty("packages").EnumerateArray(), row =>
            row.GetProperty("id").GetString() == RegistryApp.PackageId && row.GetProperty("downloads").GetInt32() >= 1);
    }

    [Fact]
    public async Task Missing_install_and_wrong_content_type_are_refused()
    {
        var client = app.CreateClient();
        var missing = await client.PostAsJsonAsync("/api/v1/downloads", new { kind = "oamod" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var body = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_field", body.GetProperty("error").GetString());
        Assert.Equal("install", body.GetProperty("field").GetString());

        var text = new StringContent("{}", System.Text.Encoding.UTF8, "text/plain");
        var refused = await client.PostAsync("/api/v1/downloads", text);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, refused.StatusCode);
    }

    [Fact]
    public async Task Admin_pages_require_sign_in()
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location?.OriginalString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mutator_kind_is_accepted_for_download()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var packages = scope.ServiceProvider.GetRequiredService<PackageAdminService>();
        var publisher = scope.ServiceProvider.GetRequiredService<CataloguePublisher>();

        await using var clash = MutatorZip("example-mod", "Example Mod");
        var refused = await packages.UploadAsync(clash, "example-mod.oamut", null, CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("already used", refused.Message, StringComparison.Ordinal);

        await using var zip = MutatorZip("steady-hands", "Steady Hands");
        var uploaded = await packages.UploadAsync(zip, "steady-hands.oamut", null, CancellationToken.None);
        Assert.True(uploaded.Ok, uploaded.Message);

        var published = await publisher.PublishAsync(CancellationToken.None);
        Assert.True(published.Ok, string.Join("; ", published.Problems));

        await using var read = app.Services.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<RegistryDb>();
        var row = await db.Packages.SingleAsync(p => p.PackageId == "steady-hands");
        Assert.False(string.IsNullOrEmpty(row.BadgeSha));

        var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/downloads", new
        {
            install = Guid.NewGuid().ToString("D"),
            package = "steady-hands",
            kind = "oamut",
            release = row.Release,
            sha256 = row.Sha256,
            engine = "0.8.0",
            platform = "windows",
            arch = "x64",
            language = "en",
            reason = "install"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var catalogue = await client.GetFromJsonAsync<JsonElement>("/v1/catalogue.json");
        var entry = catalogue.GetProperty("packages").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "steady-hands");
        Assert.Equal("oamut", entry.GetProperty("kind").GetString());
        Assert.EndsWith(".oamut", entry.GetProperty("file").GetString());
        var file = await client.GetAsync(entry.GetProperty("file").GetString());
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
    }

    private static MemoryStream MutatorZip(string id, string name)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("oamut.yaml");
            using (var writer = new StreamWriter(entry.Open()))
            {
                writer.Write($$"""
                    oamut: 1
                    id: {{id}}
                    name: {{name}}
                    version: "1.2"
                    description: Steadier aim.
                    author: {name: Ada}
                    tags: [aim]
                    icon: icon.png
                    options:
                      sway:
                        label: Sway
                        type: number
                        min: 0
                        max: 2
                        step: 0.5
                        default: 1
                    """);
            }

            using var icon = zip.CreateEntry("icon.png").Open();
            icon.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        }

        stream.Position = 0;
        return stream;
    }

    private async Task<HttpResponseMessage> PostDownload(HttpClient client, string install, int release = 1, string? package = null)
    {
        return await client.PostAsJsonAsync("/api/v1/downloads", new
        {
            install,
            package = package ?? RegistryApp.PackageId,
            kind = "oamod",
            release,
            sha256 = app.PackageSha,
            engine = "0.8.0",
            platform = "windows",
            arch = "x64",
            language = "en",
            reason = "install"
        });
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("error").GetString();
    }
}
