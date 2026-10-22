using System.Globalization;
using System.Text.Json;

namespace TaRegistry.Core.Protocol;

public sealed record DownloadResultRequest(string Result, long Bytes, int Seconds);

public abstract record ResultValidation
{
    public sealed record Ok(DownloadResultRequest Request) : ResultValidation;
    public sealed record Invalid(string Field) : ResultValidation;
    public sealed record NotObject : ResultValidation;
}

public static class ResultRequestValidator
{
    private static readonly string[] Results = ["installed", "refused", "failed", "cancelled"];

    public static ResultValidation Validate(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return new ResultValidation.NotObject();

        if (!body.TryGetProperty("result", out var resultEl) || resultEl.ValueKind != JsonValueKind.String)
            return new ResultValidation.Invalid("result");

        var result = resultEl.GetString();
        if (result is null || !Results.Contains(result))
            return new ResultValidation.Invalid("result");

        if (!body.TryGetProperty("bytes", out var bytesEl) || bytesEl.ValueKind != JsonValueKind.Number)
            return new ResultValidation.Invalid("bytes");

        if (!long.TryParse(bytesEl.GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes < 0 || bytes > (1L << 40))
            return new ResultValidation.Invalid("bytes");

        if (!body.TryGetProperty("seconds", out var secondsEl) || secondsEl.ValueKind != JsonValueKind.Number)
            return new ResultValidation.Invalid("seconds");

        if (!int.TryParse(secondsEl.GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 0 || seconds > 604800)
            return new ResultValidation.Invalid("seconds");

        return new ResultValidation.Ok(new DownloadResultRequest(result, bytes, seconds));
    }
}
