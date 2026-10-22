using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Core;
using TaRegistry.Web.Data;
using TaRegistry.Web.Services;
using TaRegistry.Web.Storage;

namespace TaRegistry.Web.Endpoints;

public static class RegistryEndpoints
{
    public static void MapRegistry(this WebApplication app)
    {
        app.MapGet("/registry.yaml", async (HttpContext http, DescriptorService descriptors, CancellationToken cancellationToken) =>
        {
            var yaml = await descriptors.TextAsync(cancellationToken);
            http.Response.ContentType = "text/yaml; charset=utf-8";
            await http.Response.WriteAsync(yaml, cancellationToken);
        });

        app.MapGet("/v1/catalogue.json", async (HttpContext http, RegistryDb db, CancellationToken cancellationToken) =>
        {
            var state = await db.CatalogueStates.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (state?.Json is null)
            {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            http.Response.Headers.CacheControl = "public, max-age=60";
            http.Response.ContentType = "application/json; charset=utf-8";
            await http.Response.Body.WriteAsync(state.Json, cancellationToken);
        });

        app.MapGet("/v1/catalogue.json.sig", async (HttpContext http, RegistryDb db, CancellationToken cancellationToken) =>
        {
            var state = await db.CatalogueStates.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (string.IsNullOrEmpty(state?.Signature))
            {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            http.Response.Headers.CacheControl = "public, max-age=60";
            http.Response.ContentType = "text/plain; charset=us-ascii";
            await http.Response.WriteAsync(state.Signature, cancellationToken);
        });

        app.MapGet("/v1/p/{file}", (HttpContext http, string file, FileBlobStore blobs) =>
        {
            if (!TryPackageFile(file, out var sha, out var kind))
                return Results.NotFound();

            var path = blobs.PackagePath(sha, kind);
            if (!File.Exists(path))
                return Results.NotFound();

            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(path, "application/zip", file, enableRangeProcessing: true);
        });

        app.MapGet("/v1/i/{file}", (HttpContext http, string file, FileBlobStore blobs) =>
        {
            if (file.Length != 68 || !file.EndsWith(".png", StringComparison.Ordinal))
                return Results.NotFound();

            var sha = file[..64];
            if (!sha.All(Uri.IsHexDigit) || sha.Any(char.IsUpper))
                return Results.NotFound();

            var path = blobs.PicturePath(sha);
            if (!File.Exists(path))
                return Results.NotFound();

            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(path, "image/png", file, enableRangeProcessing: true);
        });

        app.MapGet("/d/{token}", async (string token, DownloadService downloads, CancellationToken cancellationToken) =>
            await downloads.OpenTokenAsync(token, cancellationToken));

        var api = app.MapGroup("/api/v1");
        api.MapPost("/downloads", async (HttpContext http, DownloadService downloads, CancellationToken cancellationToken) =>
            Send(http, await downloads.RequestKeyAsync(http.Request, cancellationToken)));

        api.MapPost("/downloads/{id}/result", async (string id, HttpContext http, DownloadService downloads, CancellationToken cancellationToken) =>
            Send(http, await downloads.RecordResultAsync(id, http.Request, cancellationToken)));

        api.MapGet("/challenges/{code}", async (string code, DownloadService downloads, CancellationToken cancellationToken) =>
            (await downloads.GetChallengeAsync(code, cancellationToken)).Body);

        api.MapPost("/challenges/{code}/solve", async (string code, DownloadService downloads, CancellationToken cancellationToken) =>
            (await downloads.SolveChallengeAsync(code, cancellationToken)).Body);

        api.MapGet("/counts", async (HttpContext http, DownloadService downloads, CancellationToken cancellationToken) =>
            await downloads.CountsAsync(http, cancellationToken));
    }

    private static IResult Send(HttpContext http, EndpointResult result)
    {
        if (result.RetryAfter is int seconds)
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return result.Body;
    }

    private static bool TryPackageFile(string file, out string sha, out string kind)
    {
        sha = "";
        kind = "";
        var dot = file.LastIndexOf('.');
        if (dot != 64)
            return false;

        sha = file[..dot];
        var extension = file[(dot + 1)..];
        if (sha.Any(c => !Uri.IsHexDigit(c) || char.IsUpper(c)))
            return false;

        return PackageKinds.TryFromExtension(extension, out kind);
    }
}
