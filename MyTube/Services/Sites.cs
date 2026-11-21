namespace MyTube.Services;

/// <summary>左メニューのサイトごとの項目（アイコンは Segoe Fluent Icons の文字）。</summary>
public sealed record SiteCommand(string Label, string Icon, string Url);

/// <summary>
/// アプリで見られる動画サイト。左端の帯にこの順で並ぶ。
/// Domains はアプリの中で開いてよいドメイン（ログイン画面を含む）。それ以外は既定のブラウザで開く。
/// </summary>
public sealed record Site(string Key, string Name, string Badge, string FaviconUrl, string[] Domains, SiteCommand[] Commands)
{
    public string HomeUrl => Commands[0].Url;

    public bool Contains(Uri uri) => Domains.Any(d =>
        uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
}

public static class Sites
{
    private const string Home = "";

    /// <summary>YouTube の項目は作り込みが多いので MainWindow.xaml に直接書いている（ここの Commands は「トップ」用）。</summary>
    public static readonly Site YouTube = new("youtube", "YouTube", "Y", "https://www.youtube.com/favicon.ico",
        ["youtube.com", "youtu.be"],
        [new("ホーム", Home, "https://www.youtube.com/")]);

    public static readonly Site[] All =
    [
        YouTube,
        new("niconico", "ニコニコ動画", "ニ", "https://www.nicovideo.jp/favicon.ico",
            ["nicovideo.jp", "nimg.jp", "dwango.jp"],
            [
                new("トップ", Home, "https://www.nicovideo.jp/"),
                new("ランキング", "", "https://www.nicovideo.jp/ranking"),
            ]),
        new("tver", "TVer", "T", "https://tver.jp/favicon.ico",
            ["tver.jp"],
            [
                new("ホーム", Home, "https://tver.jp/"),
                new("マイページ", "", "https://tver.jp/mypage/fav"),
                new("リアルタイム", "", "https://tver.jp/live"),
            ]),
        new("netflix", "Netflix", "N", "https://www.netflix.com/favicon.ico",
            ["netflix.com"],
            [new("トップ", Home, "https://www.netflix.com/browse")]),
        new("disneyplus", "Disney+", "D", "https://www.disneyplus.com/favicon.ico",
            ["disneyplus.com", "disney.com", "go.com", "bamgrid.com"],
            [new("トップ", Home, "https://www.disneyplus.com/")]),
        // favicon は青い丸に再生マークだけで分かりにくいので、「prime video」の文字入りのホーム画面用アイコンを使う
        new("primevideo", "Prime Video", "P",
            "https://m.media-amazon.com/images/G/01/digital/video/DVUI/favicons/apple-touch-icon-152x152.png",
            ["amazon.co.jp", "primevideo.com", "amazon.com"],
            [new("トップ", Home, "https://www.amazon.co.jp/gp/video/storefront")]),
        new("hulu", "Hulu", "H", "https://www.hulu.jp/favicon.ico",
            ["hulu.jp"],
            [new("トップ", Home, "https://www.hulu.jp/")]),
        new("twitch", "Twitch", "T", "https://www.twitch.tv/favicon.ico",
            ["twitch.tv"],
            [
                new("ホーム", Home, "https://www.twitch.tv/"),
                new("フォロー中", "", "https://www.twitch.tv/directory/following"),
                new("ブラウズ", "", "https://www.twitch.tv/directory"),
            ]),
    ];

    /// <summary>uri がどのサイトのページか（どれでもなければ null）。</summary>
    public static Site? Find(Uri uri) => All.FirstOrDefault(s => s.Contains(uri));

    public static Site? Find(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Find(uri) : null;

    /// <summary>
    /// YouTube 以外のサイトのページか。http も含める（Disney+ はログイン中だと http の /home を経由して
    /// https に戻るので、http を止めるとそこで既定のブラウザに回ってしまう）。
    /// </summary>
    public static bool IsOtherSite(Uri uri) =>
        uri.Scheme is "https" or "http" && Find(uri) is { } s && s != YouTube;
}
