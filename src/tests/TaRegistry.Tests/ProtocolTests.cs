using System.Text.Json;
using TaRegistry.Core.Catalogue;
using TaRegistry.Core.Protocol;
using TaRegistry.Core.Signing;

namespace TaRegistry.Tests;

public class ProtocolTests
{
    [Fact]
    public void Ed25519_matches_rfc8032_test_vector()
    {
        var seed = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        var key = Ed25519Key.FromSeed("rfc8032-test-1", seed);
        Assert.Equal("ed25519:11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=", key.PublicText);
        Assert.Equal("21fe · 31df · a154 · a261", key.Fingerprint);

        var expected = Convert.FromHexString("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        var signature = key.Sign([]);
        Assert.Equal(expected, signature);
        Assert.True(Ed25519Key.Verify(key.PublicKey, [], signature));
    }

    [Fact]
    public void Sealed_key_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".key");
        try
        {
            var key = Ed25519Key.Generate("publisher-2026");
            SealedKeyFile.Write(path, key, "test-passphrase-value");
            var opened = SealedKeyFile.Read(path, "test-passphrase-value");
            Assert.Equal(key.PublicText, opened.PublicText);
            Assert.Equal(key.Sign("catalogue"u8.ToArray()), opened.Sign("catalogue"u8.ToArray()));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Catalogue_bytes_are_stable()
    {
        var draft = SampleDraft();
        var first = CatalogueWriter.Write(draft);
        var second = CatalogueWriter.Write(draft);
        Assert.Equal(first, second);
        Assert.NotEqual(0xEF, first[0]);
    }

    [Fact]
    public void Missing_install_is_reported_before_later_fields()
    {
        using var json = JsonDocument.Parse("""{"kind":"nope","package":""}""");
        var result = DownloadRequestValidator.Validate(json.RootElement, installRequired: true);
        var invalid = Assert.IsType<DownloadValidation.Invalid>(result);
        Assert.Equal("install", invalid.Field);
    }

    [Fact]
    public void Sha256_is_accepted_in_either_case()
    {
        using var json = JsonDocument.Parse("""
            {
              "install": "7f3a90d2-5b1c-4e8a-9d2f-0a6b3c4dc91e",
              "package": "example-mod",
              "kind": "oamod",
              "release": 3,
              "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "engine": "0.8.0",
              "platform": "linux",
              "arch": "x64",
              "language": "en",
              "reason": "install"
            }
            """);
        var result = DownloadRequestValidator.Validate(json.RootElement, installRequired: true);
        var ok = Assert.IsType<DownloadValidation.Ok>(result);
        Assert.Equal(new string('a', 64), ok.Request.Sha256);
    }

    [Fact]
    public void Fractional_release_is_rejected()
    {
        using var json = JsonDocument.Parse("""
            {
              "install": "7f3a90d2-5b1c-4e8a-9d2f-0a6b3c4dc91e",
              "package": "example-mod",
              "kind": "oamod",
              "release": 1.5,
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "engine": "0.8.0",
              "platform": "linux",
              "arch": "x64",
              "language": "en",
              "reason": "install"
            }
            """);
        var result = DownloadRequestValidator.Validate(json.RootElement, installRequired: true);
        var invalid = Assert.IsType<DownloadValidation.Invalid>(result);
        Assert.Equal("release", invalid.Field);
    }

    private static CatalogueDraft SampleDraft() => new()
    {
        RegistryId = "testreg",
        Sequence = 1,
        GeneratedUtc = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero),
        ExpiresUtc = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero),
        Downloads = "http://127.0.0.1:5080/",
        Keys =
        [
            new PublicKeyRecord { Id = "publisher-2026", Public = "ed25519:11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=" }
        ],
        Packages =
        [
            new CataloguePackage
            {
                Kind = "oamod",
                Id = "example-mod",
                Name = "Example Mod",
                Release = 1,
                Size = 42,
                Sha256 = new string('a', 64),
                Version = "1.0.0",
                Revision = 1
            }
        ]
    };
}
