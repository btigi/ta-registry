using System.IO.Compression;
using System.Text.Json;
using TaRegistry.Core.Catalogue;
using TaRegistry.Core.Packages;

namespace TaRegistry.Tests;

public class PackageInspectorTests
{
    private const string MappyManifest = """
        oamap: 1
        id: "x"
        name: "[MS] Schadenfreude"
        version: "1.0"
        description: "xx"
        author: {name: "unknown"}
        packaging: {revision: 1, date: "2026-10-20", packager: "Mappy 2.1.1.2"}
        maps:
          - stem: "xxx"
            name: "xxxxx"
            players: 10
            size: 17x15
            description: "xxxx"
            preview: "previews/xxx.png"
            files:
              - "maps/xxx.ota"
              - "maps/xxx.tnt"
        """;

    [Fact]
    public void Map_pack_manifest_becomes_a_valid_catalogue_entry()
    {
        var path = WritePack(MappyManifest);
        try
        {
            var inspection = PackageInspector.Inspect(path);
            Assert.Equal("unknown", inspection.Author);
            Assert.Equal("xx", inspection.Summary);

            using var details = JsonDocument.Parse(inspection.DetailsJson!);
            var map = details.RootElement.GetProperty("maps")[0];
            Assert.Equal("xxx@x", map.GetProperty("map").GetString());
            Assert.Equal(JsonValueKind.Number, map.GetProperty("players").ValueKind);
            Assert.Equal(10, map.GetProperty("players").GetInt32());
            Assert.Equal("17x15", map.GetProperty("size").GetString());
            Assert.False(map.TryGetProperty("files", out _));
            Assert.False(map.TryGetProperty("stem", out _));

            var preview = Assert.Single(inspection.MapPreviews);
            Assert.Equal(0, preview.MapIndex);
            Assert.Equal("previews/xxx.png", preview.ArchivePath);

            var problems = CatalogueRules.Problems(new CataloguePackage
            {
                Kind = inspection.Kind,
                Id = inspection.PackageId,
                Name = inspection.Name,
                Release = 1,
                Size = inspection.Size,
                Sha256 = inspection.Sha256,
                Version = inspection.Version,
                DetailsJson = inspection.DetailsJson
            });
            Assert.Empty(problems);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Quoted_numbers_stay_strings()
    {
        var path = WritePack(MappyManifest.Replace("players: 10", "players: \"10\""));
        try
        {
            var inspection = PackageInspector.Inspect(path);
            using var details = JsonDocument.Parse(inspection.DetailsJson!);
            Assert.Equal(JsonValueKind.String, details.RootElement.GetProperty("maps")[0].GetProperty("players").ValueKind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WritePack(string manifest)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".oamap");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("oamap.yaml").Open()))
            writer.Write(manifest);
        using (var stream = zip.CreateEntry("previews/xxx.png").Open())
            stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }
}
