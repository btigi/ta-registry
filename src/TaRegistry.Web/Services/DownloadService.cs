using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Core;
using TaRegistry.Core.Protocol;
using TaRegistry.Web.Data;
using TaRegistry.Web.Storage;

namespace TaRegistry.Web.Services;

public sealed record EndpointResult(IResult Body, int? RetryAfter = null);

public sealed class DownloadService(RegistryDb db, FileBlobStore blobs, TimeProvider clock)
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(15);

    public async Task<EndpointResult> RequestKeyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var body = await JsonBodies.ReadObjectAsync(request, cancellationToken);
        if (body.Error is not null)
            return new EndpointResult(body.Error);

        using var document = body.Document!;
        var settings = await db.Settings.AsNoTracking().SingleAsync(cancellationToken);
        var catalogue = await db.CatalogueStates.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (catalogue?.Json is null)
            return Error(503, "catalogue_unavailable", "The registry has no catalogue yet.");

        var validation = DownloadRequestValidator.Validate(document.RootElement, settings.InstallIdRequired);
        switch (validation)
        {
            case DownloadValidation.NotObject:
                return Error(400, "bad_request", "The body is not a JSON object.");
            case DownloadValidation.Invalid invalid:
                return Error(400, "invalid_field", $"The {invalid.Field} field is not valid.", invalid.Field);
            case DownloadValidation.Ok ok:
                return await IssueAsync(settings, ok.Request, cancellationToken);
            default:
                return Error(500, "internal", "The registry failed.");
        }
    }

    public async Task<EndpointResult> RecordResultAsync(string id, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!RegistryIds.IsDownloadId(id))
            return Error(404, "unknown_download", "The download id is not known.");

        var body = await JsonBodies.ReadObjectAsync(request, cancellationToken);
        if (body.Error is not null)
            return new EndpointResult(body.Error);

        using var document = body.Document!;
        var validation = ResultRequestValidator.Validate(document.RootElement);
        switch (validation)
        {
            case ResultValidation.NotObject:
                return Error(400, "bad_request", "The body is not a JSON object.");
            case ResultValidation.Invalid invalid:
                return Error(400, "invalid_field", $"The {invalid.Field} field is not valid.", invalid.Field);
        }

        var parsed = (ResultValidation.Ok)validation;
        var download = await db.Downloads.SingleOrDefaultAsync(d => d.DownloadId == id, cancellationToken);
        if (download is null)
            return Error(404, "unknown_download", "The download id is not known.");

        if (download.Result is not null)
            return Error(409, "result_recorded", "A result is already stored.");

        download.Result = parsed.Request.Result;
        download.ResultBytes = parsed.Request.Bytes;
        download.ResultSeconds = parsed.Request.Seconds;
        download.ResultAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return new EndpointResult(Results.NoContent());
    }

    public async Task<EndpointResult> GetChallengeAsync(string code, CancellationToken cancellationToken)
    {
        if (!RegistryIds.IsChallengeCode(code))
            return Error(404, "unknown_challenge", "The challenge code is not known.");

        var challenge = await db.Challenges.SingleOrDefaultAsync(c => c.Code == code, cancellationToken);
        if (challenge is null)
            return Error(404, "unknown_challenge", "The challenge code is not known.");

        var now = clock.GetUtcNow().UtcDateTime;
        if (challenge.State == "pending" && challenge.ExpiresAt <= now)
            challenge.State = "expired";

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);

        return new EndpointResult(Results.Json(new Dictionary<string, object?>
        {
            ["code"] = challenge.Code,
            ["state"] = challenge.State,
            ["expires"] = UtcTimes.Format(challenge.ExpiresAt)
        }));
    }

    public async Task<EndpointResult> SolveChallengeAsync(string code, CancellationToken cancellationToken)
    {
        if (!RegistryIds.IsChallengeCode(code))
            return Error(404, "unknown_challenge", "The challenge code is not known.");

        var challenge = await db.Challenges.SingleOrDefaultAsync(c => c.Code == code, cancellationToken);
        if (challenge is null)
            return Error(404, "unknown_challenge", "The challenge code is not known.");

        var now = clock.GetUtcNow().UtcDateTime;
        if (challenge.State != "solved" && challenge.ExpiresAt <= now)
        {
            challenge.State = "expired";
            await db.SaveChangesAsync(cancellationToken);
            return Error(410, "challenge_expired", "The challenge has ended.");
        }

        challenge.State = "solved";
        await db.SaveChangesAsync(cancellationToken);
        return new EndpointResult(Results.Json(new Dictionary<string, object?>
        {
            ["code"] = challenge.Code,
            ["state"] = "solved"
        }));
    }

    public async Task<IResult> OpenTokenAsync(string token, CancellationToken cancellationToken)
    {
        var download = await db.Downloads.AsNoTracking().SingleOrDefaultAsync(d => d.Token == token, cancellationToken);
        if (download is null || download.ExpiresAt <= clock.GetUtcNow().UtcDateTime)
            return Results.NotFound();

        var path = blobs.PackagePath(download.Sha256, download.Kind);
        if (!File.Exists(path))
            return Results.NotFound();

        return Results.File(path, "application/zip", PackageKinds.FileName(download.Sha256, download.Kind), enableRangeProcessing: true);
    }

    public async Task<IResult> CountsAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var rows = await db.Downloads.AsNoTracking()
            .GroupBy(d => new { d.Kind, d.PackageId })
            .Select(g => new { g.Key.Kind, g.Key.PackageId, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var packages = rows
            .OrderBy(r => r.Kind, StringComparer.Ordinal)
            .ThenBy(r => r.PackageId, StringComparer.Ordinal)
            .Select(r => new Dictionary<string, object?>
            {
                ["kind"] = r.Kind,
                ["id"] = r.PackageId,
                ["downloads"] = r.Count
            })
            .ToList();

        http.Response.Headers.CacheControl = "public, max-age=300";
        return Results.Json(new Dictionary<string, object?>
        {
            ["counts"] = 1,
            ["generated"] = UtcTimes.Format(clock.GetUtcNow()),
            ["packages"] = packages
        });
    }

    private async Task<EndpointResult> IssueAsync(RegistrySettings settings, DownloadRequest request, CancellationToken cancellationToken)
    {
        var package = await db.Packages.AsNoTracking().SingleOrDefaultAsync(
            p => p.InCatalogue && p.Kind == request.Kind && p.PackageId == request.Package,
            cancellationToken);

        if (package is null)
            return Error(404, "unknown_package", "No package with this id and kind is offered.");

        if (package.Release != request.Release || !string.Equals(package.Sha256, request.Sha256, StringComparison.Ordinal))
            return Error(409, "release_mismatch", "The release and digest do not match the catalogue.", currentRelease: package.Release);

        var now = clock.GetUtcNow().UtcDateTime;
        if (!string.IsNullOrEmpty(request.Install))
        {
            var limited = await ApplyRateLimitAsync(settings, request.Install, now, cancellationToken);
            if (limited is not null)
                return limited;
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        DownloadEntity? download = null;
        if (!string.IsNullOrEmpty(request.Install))
        {
            download = await db.Downloads.SingleOrDefaultAsync(d =>
                d.InstallId == request.Install &&
                d.Kind == request.Kind &&
                d.PackageId == request.Package &&
                d.Release == request.Release &&
                d.Sha256 == request.Sha256 &&
                d.Result == null, cancellationToken);
        }

        var expires = now.AddSeconds(settings.DownloadUrlTtlSeconds);
        if (download is null)
        {
            download = new DownloadEntity
            {
                DownloadId = await UniqueDownloadIdAsync(cancellationToken),
                InstallId = request.Install ?? "",
                Kind = request.Kind,
                PackageId = request.Package,
                Release = request.Release,
                Sha256 = request.Sha256,
                Token = await UniqueTokenAsync(cancellationToken),
                ExpiresAt = expires,
                CreatedAt = now,
                KeyCount = 1
            };
            db.Downloads.Add(download);
        }
        else
        {
            download.Token = await UniqueTokenAsync(cancellationToken);
            download.ExpiresAt = expires;
            download.KeyCount += 1;
        }

        if (!string.IsNullOrEmpty(request.Install))
        {
            var cutoff = now.AddHours(-2);
            await db.IssuedKeys.Where(k => k.IssuedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
            db.IssuedKeys.Add(new IssuedKey
            {
                InstallId = request.Install,
                DownloadId = download.DownloadId,
                IssuedAt = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        var url = settings.BaseUrl.TrimEnd('/') + "/d/" + download.Token;
        return new EndpointResult(Results.Json(new Dictionary<string, object?>
        {
            ["download"] = download.DownloadId,
            ["expires"] = UtcTimes.Format(expires),
            ["url"] = url
        }, statusCode: StatusCodes.Status201Created));
    }

    private async Task<EndpointResult?> ApplyRateLimitAsync(RegistrySettings settings, string install, DateTime now, CancellationToken cancellationToken)
    {
        var windowStart = now.AddHours(-1);
        var issued = await db.IssuedKeys.AsNoTracking()
            .Where(k => k.InstallId == install && k.IssuedAt >= windowStart)
            .OrderBy(k => k.IssuedAt)
            .Select(k => k.IssuedAt)
            .ToListAsync(cancellationToken);

        if (settings.KeysPerHour > 0 && issued.Count >= settings.KeysPerHour)
        {
            var retry = (int)Math.Ceiling((issued[0].AddHours(1) - now).TotalSeconds);
            if (retry < 60)
                retry = 60;
            return Error(429, "rate_limited", "Too many download keys were requested. Try again later.", retryAfter: retry);
        }

        if (settings.ChallengeAfter >= 0 && issued.Count >= settings.ChallengeAfter)
        {
            var solved = await db.Challenges.AnyAsync(c =>
                c.InstallId == install && c.State == "solved" && c.CreatedAt >= windowStart, cancellationToken);
            if (!solved)
                return await ChallengeAsync(settings, install, now, cancellationToken);
        }

        return null;
    }

    private async Task<EndpointResult> ChallengeAsync(RegistrySettings settings, string install, DateTime now, CancellationToken cancellationToken)
    {
        var challenge = await db.Challenges
            .Where(c => c.InstallId == install && c.State == "pending" && c.ExpiresAt > now)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (challenge is null)
        {
            challenge = new ChallengeEntity
            {
                Code = await UniqueChallengeCodeAsync(cancellationToken),
                InstallId = install,
                State = "pending",
                CreatedAt = now,
                ExpiresAt = now.Add(ChallengeLifetime)
            };
            db.Challenges.Add(challenge);
            await db.SaveChangesAsync(cancellationToken);
        }

        var verify = settings.BaseUrl.TrimEnd('/') + "/verify?code=" + Uri.EscapeDataString(challenge.Code);
        return Error(428, "challenge_required", "Confirm you are a person before downloading again.", challenge: new Dictionary<string, object?>
        {
            ["code"] = challenge.Code,
            ["verify"] = verify,
            ["expires"] = UtcTimes.Format(challenge.ExpiresAt),
            ["poll"] = 3
        });
    }

    private async Task<string> UniqueDownloadIdAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 5; i++)
        {
            var id = RegistryIds.DownloadId();
            if (!await db.Downloads.AnyAsync(d => d.DownloadId == id, cancellationToken))
                return id;
        }

        throw new InvalidOperationException("Could not allocate a download id.");
    }

    private async Task<string> UniqueTokenAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 5; i++)
        {
            var token = RegistryIds.UrlToken();
            if (!await db.Downloads.AnyAsync(d => d.Token == token, cancellationToken))
                return token;
        }

        throw new InvalidOperationException("Could not allocate a download token.");
    }

    private async Task<string> UniqueChallengeCodeAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 5; i++)
        {
            var code = RegistryIds.ChallengeCode();
            if (!await db.Challenges.AnyAsync(c => c.Code == code, cancellationToken))
                return code;
        }

        throw new InvalidOperationException("Could not allocate a challenge code.");
    }

    private static EndpointResult Error(
        int status,
        string error,
        string message,
        string? field = null,
        int? currentRelease = null,
        int? retryAfter = null,
        Dictionary<string, object?>? challenge = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["error"] = error,
            ["message"] = message
        };
        if (field is not null)
            body["field"] = field;
        if (currentRelease is not null)
            body["current_release"] = currentRelease;
        if (retryAfter is not null)
            body["retry_after"] = retryAfter;
        if (challenge is not null)
            body["challenge"] = challenge;

        return new EndpointResult(Results.Json(body, statusCode: status), retryAfter);
    }
}

public readonly struct JsonBody
{
    public JsonDocument? Document { get; init; }
    public IResult? Error { get; init; }
}

public static class JsonBodies
{
    public const int MaxBytes = 8192;

    public static async Task<JsonBody> ReadObjectAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!IsJson(request.ContentType))
        {
            return new JsonBody
            {
                Error = Results.Json(new Dictionary<string, object?>
                {
                    ["error"] = "unsupported_media_type",
                    ["message"] = "Content-Type must be application/json."
                }, statusCode: StatusCodes.Status415UnsupportedMediaType)
            };
        }

        if (request.ContentLength > MaxBytes)
            return TooLarge();

        var buffer = new byte[MaxBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await request.Body.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken);
            if (n == 0)
                break;
            read += n;
        }

        if (read > MaxBytes)
            return TooLarge();

        try
        {
            var document = JsonDocument.Parse(buffer.AsMemory(0, read));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                return NotObject();
            }

            return new JsonBody { Document = document };
        }
        catch (JsonException)
        {
            return NotObject();
        }
    }

    private static bool IsJson(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        var media = contentType.Split(';')[0].Trim();
        return media.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonBody TooLarge() => new()
    {
        Error = Results.Json(new Dictionary<string, object?>
        {
            ["error"] = "too_large",
            ["message"] = "The body is larger than 8 KiB."
        }, statusCode: StatusCodes.Status413PayloadTooLarge)
    };

    private static JsonBody NotObject() => new()
    {
        Error = Results.Json(new Dictionary<string, object?>
        {
            ["error"] = "bad_request",
            ["message"] = "The body is not a JSON object."
        }, statusCode: StatusCodes.Status400BadRequest)
    };
}
