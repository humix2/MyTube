using System.Text.RegularExpressions;

namespace MyTube.Services;

public static partial class YouTubeUrl
{
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    public static bool IsYouTube(Uri uri) =>
        uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);

    /// <summary>/watch?v=ID・/shorts/ID・youtu.be/ID から動画IDを取り出す。</summary>
    public static string? GetVideoId(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsYouTube(uri)) return null;

        string? id = null;
        var path = uri.AbsolutePath;
        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            id = path.Trim('/');
        else if (path.Equals("/watch", StringComparison.OrdinalIgnoreCase))
            id = GetQuery(uri, "v");
        else if (path.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase))
            id = path["/shorts/".Length..].Split('/')[0];

        return id is not null && VideoIdRegex().IsMatch(id) ? id : null;
    }

    /// <summary>youtube.com/redirect?q=... の飛び先を返す。</summary>
    public static string? GetRedirectTarget(Uri uri) =>
        IsYouTube(uri) && uri.AbsolutePath.Equals("/redirect", StringComparison.OrdinalIgnoreCase)
            ? GetQuery(uri, "q")
            : null;

    public static string? GetQuery(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key) return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }
}
