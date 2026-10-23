using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TaRegistry.Core.Catalogue;

public static class CatalogueWriter
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static byte[] Write(CatalogueDraft draft)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("catalogue", 1);
            writer.WriteString("registry", draft.RegistryId);
            writer.WriteNumber("sequence", draft.Sequence);
            writer.WriteString("generated", UtcTimes.Format(draft.GeneratedUtc));
            writer.WriteString("expires", UtcTimes.Format(draft.ExpiresUtc));
            if (!string.IsNullOrEmpty(draft.Downloads))
                writer.WriteString("downloads", draft.Downloads);

            if (draft.Keys.Count > 0)
            {
                writer.WritePropertyName("keys");
                writer.WriteStartArray();
                foreach (var key in draft.Keys)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", key.Id);
                    writer.WriteString("public", key.Public);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WritePropertyName("packages");
            writer.WriteStartArray();
            foreach (var package in draft.Packages.OrderBy(p => p.Kind, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal))
                WritePackage(writer, package);

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WritePackage(Utf8JsonWriter writer, CataloguePackage package)
    {
        using var details = string.IsNullOrWhiteSpace(package.DetailsJson)
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(package.DetailsJson);

        var extra = details.RootElement;
        writer.WriteStartObject();
        writer.WriteString("kind", package.Kind);
        writer.WriteString("id", package.Id);
        writer.WriteString("name", package.Name);
        writer.WriteNumber("release", package.Release);
        writer.WriteNumber("size", package.Size);
        writer.WriteString("sha256", package.Sha256);
        writer.WriteString("file", "/v1/p/" + PackageKinds.FileName(package.Sha256, package.Kind));
        WriteOptionalString(writer, "version", package.Version);
        if (package.Revision is int revision)
            writer.WriteNumber("revision", revision);
        WriteOptionalString(writer, "publisher", package.Publisher);
        WriteOptionalString(writer, "author", package.Author);
        if (package.Summary is not null)
            writer.WriteString("summary", package.Summary);
        if (package.Notes is not null)
            writer.WriteString("notes", package.Notes);
        WriteOptionalString(writer, "homepage", package.Homepage);
        if (package.Tags is { Count: > 0 })
        {
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in package.Tags)
                writer.WriteStringValue(tag);
            writer.WriteEndArray();
        }

        WriteOptionalString(writer, "badge", package.Badge);
        WriteOptionalString(writer, "published", package.Published);
        WriteExtra(writer, extra, "requires");
        WriteExtra(writer, extra, "sim_hash");
        WriteExtra(writer, extra, "hacks");
        WriteExtra(writer, extra, "maps");
        WriteExtra(writer, extra, "coverage");
        WriteExtra(writer, extra, "english_name");
        WriteExtra(writer, extra, "word");
        WriteExtra(writer, extra, "locales");
        WriteExtra(writer, extra, "fallbacks");
        WriteExtra(writer, extra, "needs");
        WriteExtra(writer, extra, "extends");
        WriteExtra(writer, extra, "works_with");
        WriteExtra(writer, extra, "conflicts_with");
        WriteExtra(writer, extra, "layers");
        WriteExtra(writer, extra, "clashes");
        WriteExtra(writer, extra, "options");
        writer.WriteEndObject();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            writer.WriteString(name, value);
    }

    private static void WriteExtra(Utf8JsonWriter writer, JsonElement extra, string name)
    {
        if (extra.ValueKind == JsonValueKind.Object && extra.TryGetProperty(name, out var value))
        {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }
    }
}

public static class DescriptorWriter
{
    public static string Write(DescriptorModel model)
    {
        var builder = new StringBuilder();
        builder.Append("registry: 1\n");
        Line(builder, "id", model.Id);
        Line(builder, "name", model.Name);
        Line(builder, "title", model.Title);
        Line(builder, "description", model.Description);
        Line(builder, "author", model.Author);
        Line(builder, "homepage", model.Homepage);
        Line(builder, "catalogue", model.CatalogueUrl);
        if (model.Keys.Count > 0)
        {
            builder.Append("keys:\n");
            foreach (var key in model.Keys)
            {
                builder.Append("  - id: ").Append(Quote(key.Id)).Append('\n');
                builder.Append("    public: ").Append(Quote(key.Public)).Append('\n');
            }
        }

        builder.Append("downloads:\n");
        builder.Append("  mode: \"tokens\"\n");
        builder.Append("  api: ").Append(Quote(model.ApiUrl)).Append('\n');
        builder.Append("  install-id: ").Append(Quote(model.InstallIdRequired ? "required" : "none")).Append('\n');
        return builder.ToString();
    }

    private static void Line(StringBuilder builder, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        builder.Append(key).Append(": ").Append(Quote(value)).Append('\n');
    }

    public static string Quote(string value)
    {
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }
}
