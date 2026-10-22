using System.Security.Cryptography;
using System.Text;

namespace TaRegistry.Core;

public static class RegistryIds
{
    private const string ChallengeAlphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string DownloadId() => "d_" + Base64Url(RandomNumberGenerator.GetBytes(16));

    public static string UrlToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string ChallengeCode()
    {
        Span<char> chars = stackalloc char[9];
        var bytes = RandomNumberGenerator.GetBytes(8);
        for (var i = 0; i < 8; i++)
        {
            var index = i < 4 ? i : i + 1;
            chars[index] = ChallengeAlphabet[bytes[i] % ChallengeAlphabet.Length];
        }

        chars[4] = '-';
        return new string(chars);
    }

    public static bool IsDownloadId(string value) =>
        value.Length is >= 1 and <= 128 && value.All(IsIdChar);

    public static bool IsChallengeCode(string value) =>
        value.Length is >= 1 and <= 128 && value.All(c => IsIdChar(c));

    private static bool IsIdChar(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';

    private static string Base64Url(byte[] bytes)
    {
        var text = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return text;
    }
}
