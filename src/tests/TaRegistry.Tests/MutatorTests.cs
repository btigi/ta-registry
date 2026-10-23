using System.IO.Compression;
using System.Text.Json;
using TaRegistry.Core.Catalogue;
using TaRegistry.Core.Packages;

namespace TaRegistry.Tests;

public class MutatorTests
{
    private const string SteadyHands = """
        oamut: 1
        id: steady-hands
        name: Steady Hands
        version: "1.2"
        description: "A steadier aim.\nThe rest of the description stays out of the summary."
        author: {name: Ada}
        homepage: https://example.test/steady
        tags: [aim]
        requires:
          engine: ">= 0.8"
          mutators:
            - calm-seas >= 1.0
        works-with:
          - ta-3.1c
          - ridge >= 1.0
        conflicts-with:
          - shaky-grip
        layers: [units, weapons, not-a-layer]
        clashes: add
        options:
          grip:
            label: Grip
            type: choice
            values: [soft, firm]
            labels: [Soft, Firm]
            default: firm
          sway:
            label: Sway
            type: number
            min: 0
            max: 2
            step: 0.5
            default: 1
          mystery:
            label: Mystery
            type: colour
        icon: icon.png
        """;

    [Fact]
    public void Steady_hands_manifest_becomes_catalogue_json()
    {
        var path = WriteMutator(SteadyHands);
        try
        {
            var inspection = PackageInspector.Inspect(path);
            Assert.Equal("oamut", inspection.Kind);
            Assert.Equal("steady-hands", inspection.PackageId);
            Assert.Equal("Ada", inspection.Author);
            Assert.Equal("A steadier aim.", inspection.Summary);
            Assert.Equal("icon.png", inspection.IconPath);
            Assert.Contains(inspection.Warnings, warning => warning.Contains("mystery", StringComparison.Ordinal));

            var package = new CataloguePackage
            {
                Kind = inspection.Kind,
                Id = inspection.PackageId,
                Name = inspection.Name,
                Release = 1,
                Size = inspection.Size,
                Sha256 = inspection.Sha256,
                Version = inspection.Version,
                Author = inspection.Author,
                Summary = inspection.Summary,
                Homepage = inspection.Homepage,
                Tags = inspection.Tags,
                DetailsJson = inspection.DetailsJson
            };
            Assert.Empty(CatalogueRules.Problems(package));

            var json = CatalogueWriter.Write(new CatalogueDraft
            {
                RegistryId = "testreg",
                Sequence = 1,
                GeneratedUtc = DateTimeOffset.Parse("2026-10-22T00:00:00Z"),
                ExpiresUtc = DateTimeOffset.Parse("2026-11-21T00:00:00Z"),
                Keys = [],
                Packages = [package]
            });

            using var document = JsonDocument.Parse(json);
            var entry = document.RootElement.GetProperty("packages")[0];
            Assert.Equal("oamut", entry.GetProperty("kind").GetString());
            Assert.EndsWith(".oamut", entry.GetProperty("file").GetString());

            var prerequisite = entry.GetProperty("requires").GetProperty("mutators")[0];
            Assert.Equal("calm-seas", prerequisite.GetProperty("id").GetString());
            Assert.Equal(">= 1.0", prerequisite.GetProperty("version").GetString());

            var works = entry.GetProperty("works_with");
            Assert.Equal(JsonValueKind.Array, works.ValueKind);
            Assert.Equal("ridge >= 1.0", works[1].GetString());

            var conflict = entry.GetProperty("conflicts_with")[0];
            Assert.Equal("shaky-grip", conflict.GetProperty("id").GetString());
            Assert.Equal(2, entry.GetProperty("layers").GetArrayLength());
            Assert.Equal("add", entry.GetProperty("clashes").GetString());

            var options = entry.GetProperty("options");
            Assert.Equal(JsonValueKind.Array, options.ValueKind);
            Assert.Equal(2, options.GetArrayLength());
            var sway = options.EnumerateArray().Single(option => option.GetProperty("id").GetString() == "sway");
            Assert.Equal(JsonValueKind.Number, sway.GetProperty("min").ValueKind);
            Assert.Equal(JsonValueKind.Number, sway.GetProperty("step").ValueKind);
            Assert.Equal(0, sway.GetProperty("min").GetDouble());
            Assert.Equal(0.5, sway.GetProperty("step").GetDouble());
            Assert.DoesNotContain(entry.GetProperty("layers").EnumerateArray(), layer => layer.GetString() == "not-a-layer");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Mutator_version_must_match_the_grammar()
    {
        var problems = CatalogueRules.Problems(Sample("01.2"));
        Assert.Contains(problems, problem => problem.Contains("version", StringComparison.Ordinal));

        problems = CatalogueRules.Problems(Sample("1.2.3.4"));
        Assert.Contains(problems, problem => problem.Contains("version", StringComparison.Ordinal));

        Assert.Empty(CatalogueRules.Problems(Sample("2.0.1")));
    }

    [Fact]
    public void Package_ids_are_unique_across_kinds()
    {
        var mod = new CataloguePackage
        {
            Kind = "oamod",
            Id = "steady-hands",
            Name = "Steady Hands",
            Release = 1,
            Size = 1,
            Sha256 = new string('b', 64),
            Version = "1.0.0",
            Revision = 1
        };
        var mutator = Sample("1.2");

        var problems = CatalogueRules.DuplicateIds([mod, mutator]);
        Assert.Single(problems);
    }

    private static CataloguePackage Sample(string version) => new()
    {
        Kind = "oamut",
        Id = "steady-hands",
        Name = "Steady Hands",
        Release = 1,
        Size = 1,
        Sha256 = new string('a', 64),
        Version = version
    };

    private static string WriteMutator(string manifest)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".oamut");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("oamut.yaml").Open()))
            writer.Write(manifest);
        using (var stream = zip.CreateEntry("icon.png").Open())
            stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }
}
