using System.Text;

namespace TaRegistry.Core;

public static class TextRules
{
    public static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);

    public static bool HasControl(string value)
    {
        foreach (var c in value)
        {
            if (c < 0x20 || (c >= 0x7F && c <= 0x9F))
                return true;
        }

        return false;
    }

    public static bool IsHttpUrl(string value, bool allowQuery)
    {
        if (value.Length is < 1 or > 2048)
            return false;

        foreach (var c in value)
        {
            if (c < 0x21 || c > 0x7E)
                return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        if (uri.HostNameType == UriHostNameType.IPv6)
            return false;

        if (!allowQuery && !string.IsNullOrEmpty(uri.Query))
            return false;

        return true;
    }

    public static bool IsOrigin(string value)
    {
        if (!IsHttpUrl(value, allowQuery: false))
            return false;

        var uri = new Uri(value);
        return uri.AbsolutePath is "/" or "";
    }
}
