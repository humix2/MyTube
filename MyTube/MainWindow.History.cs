using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using MyTube.Services;

namespace MyTube;

/// <summary>
/// 視聴履歴。どのサイトでも、アプリで 30 秒以上再生した動画を記録し（common.js が知らせてくる）、
/// 左端の帯の「視聴履歴」で一覧のページを出す。
/// 一覧のページはアプリの中だけにある https://mytube.example/ のページとして、WebView2 に直接返す。
/// </summary>
public partial class MainWindow
{
    /// <summary>アプリが自分で出すページのホスト名（.example は実在しない名前として予約されている）。</summary>
    private const string AppHost = "mytube.example";
    private const string HistoryUrl = "https://" + AppHost + "/history";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private ViewingHistoryStore? _viewing;
    private ViewingHistoryStore Viewing => _viewing ??= new ViewingHistoryStore(Path.Combine(DataDir, "history.json"));

    private readonly string _historyPage = LoadScript("history.html");

    private static bool IsAppPage(Uri uri) =>
        uri.Scheme == "https" && uri.Host.Equals(AppHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>画面を作ったときに、アプリのページを返す準備をする。</summary>
    private void AttachAppPages(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter($"https://{AppHost}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnAppPageRequested;
    }

    private void OnAppPageRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_env is null || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !IsAppPage(uri)) return;

        var path = uri.AbsolutePath;
        if (path == "/history")
        {
            e.Response = Respond(Encoding.UTF8.GetBytes(_historyPage), "text/html; charset=utf-8");
        }
        else if (path == "/history.json")
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                sites = Sites.All.Select(s => new { key = s.Key, name = s.Name }),
                items = Viewing.Entries,
            }, WebJson);
            e.Response = Respond(json, "application/json; charset=utf-8");
        }
        else if (path.StartsWith("/icon/", StringComparison.Ordinal) &&
                 Sites.All.FirstOrDefault(s => s.Key == path["/icon/".Length..]) is { } site &&
                 FindSiteIconFile(site) is { } file)
        {
            // 画像の種類（ico・png）はブラウザが中身から見分ける
            e.Response = Respond(File.ReadAllBytes(file), null);
        }
        else
        {
            e.Response = _env.CreateWebResourceResponse(null, 404, "Not Found", "");
        }
    }

    private CoreWebView2WebResourceResponse Respond(byte[] body, string? contentType)
    {
        var headers = "Cache-Control: no-store";
        if (contentType is not null) headers = $"Content-Type: {contentType}\r\n" + headers;
        return _env!.CreateWebResourceResponse(new MemoryStream(body), 200, "OK", headers);
    }

    /// <summary>左端の帯のアイコンとして保存してある favicon（まだ取れていなければ null）。</summary>
    private static string? FindSiteIconFile(Site site)
    {
        try
        {
            return Directory.Exists(IconsDir)
                ? Directory.GetFiles(IconsDir, site.Key + "-*.img").FirstOrDefault()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>ページから「30 秒再生した」と知らせが来た。動画のページなら履歴に入れる。</summary>
    private void RecordWatched(Uri source, JsonElement message)
    {
        // 知らせてきたページと同じサイトの URL だけ受け付ける
        if (Sites.Find(source) is not { } site ||
            !Uri.TryCreate(GetString(message, "url", 2000), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || Sites.Find(uri) != site ||
            WatchPages.Identify(site, uri) is not (var key, var url))
            return;

        var title = WatchPages.CleanTitle(site, GetString(message, "title", 500));
        var image = WatchPages.YouTubeThumbnail(key);
        if (image is null && Uri.TryCreate(GetString(message, "image", 2000), UriKind.Absolute, out var img) &&
            img.Scheme == "https" && !WatchPages.IsGenericImage(img))
            image = img.AbsoluteUri;

        Viewing.Add(new HistoryEntry(key, site.Key, title, url, image, DateTime.UtcNow));
        NotifyHistoryChanged();

        if (site.Key == "netflix" && Viewing.Entries[0].Image is null)
            _ = FetchNetflixImageAsync(key);
    }

    /// <summary>
    /// Netflix の再生画面にはサムネイルになる画像がないので、作品ページ（/title/ID）の og:image を使う。
    /// 話数の ID でもシリーズの作品ページに転送される。作品ページはログインしていなくても見られる。
    /// </summary>
    private async Task FetchNetflixImageAsync(string key)
    {
        var id = key["netflix:".Length..];
        if (!id.All(char.IsAsciiDigit)) return;
        try
        {
            var html = await IconClient.GetStringAsync($"https://www.netflix.com/title/{id}");
            var m = System.Text.RegularExpressions.Regex.Match(html,
                """<meta[^>]+property="og:image"[^>]+content="([^"]+)""");
            if (!m.Success) return;
            var image = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            // 画像は Netflix の配信サーバー（nflxso.net）のものだけ使う
            if (Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                (uri.Host == "nflxso.net" || uri.Host.EndsWith(".nflxso.net", StringComparison.OrdinalIgnoreCase)) &&
                Viewing.SetImage(key, uri.AbsoluteUri))
                NotifyHistoryChanged();
        }
        catch (Exception)
        {
            // 取れなければサイトのアイコンのまま（次に記録するときにまた試す）
        }
    }

    /// <summary>視聴履歴のページから来た操作（削除・すべて消去）。</summary>
    private void OnHistoryPageMessage(VideoPane pane, JsonElement message)
    {
        switch (GetString(message, "type"))
        {
            case "activate":
                SetActivePane(pane, focus: false);
                break;
            case "gesture":
                HandleGesture(pane, message);
                break;
            case "historyRemove" when GetString(message, "key", 500) is { } key:
                if (Viewing.Remove(key)) NotifyHistoryChanged();
                break;
            case "historyClear":
                Viewing.Clear();
                NotifyHistoryChanged();
                break;
        }
    }

    /// <summary>開いている視聴履歴のページに、読み直すよう知らせる。</summary>
    private void NotifyHistoryChanged()
    {
        foreach (var p in AllPanes)
        {
            if (p.Core is { } core && Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) && IsAppPage(uri))
                core.PostWebMessageAsJson("""{"type":"historyChanged"}""");
        }
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        CloseFlyout();
        ActiveCore?.Navigate(HistoryUrl);
    }
}
