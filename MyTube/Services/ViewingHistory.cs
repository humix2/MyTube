using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyTube.Services;

/// <summary>視聴履歴の 1 件。同じ動画（Key）は最後に見た 1 件だけを残す。</summary>
public sealed record HistoryEntry(string Key, string Site, string Title, string Url, string? Image, DateTime WatchedAt);

/// <summary>
/// どのサイトでも、アプリで 30 秒以上再生した動画を記録する（左端の帯の「視聴履歴」で一覧にする）。
/// YouTube の視聴済み表示（<see cref="WatchHistoryStore"/>）とは別もの。
/// </summary>
public sealed class ViewingHistoryStore
{
    public const int MaxEntries = 1000;

    private readonly string _path;
    private readonly List<HistoryEntry> _entries; // 新しい順

    public ViewingHistoryStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    public IReadOnlyList<HistoryEntry> Entries => _entries;

    /// <summary>記録する。同じ動画がもうあれば先頭に移す（新しいタイトル・画像がないときは前のものを使う）。</summary>
    public void Add(HistoryEntry entry)
    {
        var index = _entries.FindIndex(e => e.Key == entry.Key);
        if (index >= 0)
        {
            var old = _entries[index];
            _entries.RemoveAt(index);
            entry = entry with
            {
                Title = string.IsNullOrEmpty(entry.Title) ? old.Title : entry.Title,
                Image = entry.Image ?? old.Image,
            };
        }
        _entries.Insert(0, entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Save();
    }

    /// <summary>あとから取れたサムネイルを付ける（もう画像があるときや、消されたあとは何もしない）。</summary>
    public bool SetImage(string key, string image)
    {
        var index = _entries.FindIndex(e => e.Key == key);
        if (index < 0 || _entries[index].Image is not null) return false;
        _entries[index] = _entries[index] with { Image = image };
        Save();
        return true;
    }

    public bool Remove(string key)
    {
        if (_entries.RemoveAll(e => e.Key == key) == 0) return false;
        Save();
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    private static List<HistoryEntry> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path)) ?? [];
                return [.. list.Where(e => e.Key is not null && e.Url is not null).OrderByDescending(e => e.WatchedAt)];
            }
        }
        catch (Exception)
        {
            // 壊れていたら空から始める
        }
        return [];
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception)
        {
            // 次に保存するときに書かれるので止めない
        }
    }
}

/// <summary>
/// 動画のページかどうかを URL で見分ける。一覧やトップのページでも予告編などが自動で流れるので、
/// 再生した時間だけでなく、URL が動画のページの形をしているときだけ記録する。
/// </summary>
public static partial class WatchPages
{
    // 「ホスト名 + パス」に当てる。(?<id>) があればそれを動画の ID にする
    private static readonly Dictionary<string, Regex> Patterns = new()
    {
        ["niconico"] = NiconicoRegex(),
        ["tver"] = TVerRegex(),
        ["netflix"] = NetflixRegex(),
        ["disneyplus"] = DisneyPlusRegex(),
        ["primevideo"] = PrimeVideoRegex(),
        ["hulu"] = HuluRegex(),
        ["twitch"] = TwitchRegex(),
        // NHK ONE は URL の決まりがまだ分からないので、どのページでも記録する
        // （トップの /tv/g1 などは同時配信の放送をそのまま流すページなので、記録して困らない）
    };

    [GeneratedRegex(@"^(www\.|sp\.)?nicovideo\.jp/watch/(?<id>[a-z]{2}\d+)|^live\.nicovideo\.jp/watch/(?<id>lv\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NiconicoRegex();

    // /live だけ（リアルタイムの一覧）は除き、/live/〜 の放送のページは記録する
    [GeneratedRegex(@"^tver\.jp/(episodes/(?<id>[^/]+)|live/.+)", RegexOptions.IgnoreCase)]
    private static partial Regex TVerRegex();

    [GeneratedRegex(@"^www\.netflix\.com/watch/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NetflixRegex();

    [GeneratedRegex(@"^www\.disneyplus\.com/(.*/)?(play|video)/(?<id>[^/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DisneyPlusRegex();

    // 作品の ID は 10 文字（B0…）のものと、もっと長いもの（0PDKOC…）がある
    [GeneratedRegex(@"^www\.amazon\.co\.jp/(.*/)?(gp/video/detail|dp)/(?<id>[A-Z0-9]{10,})", RegexOptions.IgnoreCase)]
    private static partial Regex PrimeVideoRegex();

    [GeneratedRegex(@"^www\.hulu\.jp/watch/(?<id>[^/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HuluRegex();

    // 過去の配信（/videos/ID）と、配信中のチャンネル（/チャンネル名）。チャンネル名と紛らわしいメニューのページは除く
    [GeneratedRegex(@"^(www\.)?twitch\.tv/(videos/(?<id>\d+)|(?<id>(?!(directory|settings|search|downloads|subscriptions|inventory|wallet|drops|friends|messages|turbo|prime|store|jobs|login|signup|videos|p)/?$)[a-z0-9_]{3,25})/?$)", RegexOptions.IgnoreCase)]
    private static partial Regex TwitchRegex();

    /// <summary>動画のページなら、履歴の Key と開き直すときの URL。そうでなければ null。</summary>
    public static (string Key, string Url)? Identify(Site site, Uri uri)
    {
        if (site == Sites.YouTube)
        {
            return YouTubeUrl.GetVideoId(uri.AbsoluteUri) is { } id
                ? ("youtube:" + id, "https://www.youtube.com/watch?v=" + id)
                : null;
        }

        // 再生位置などのクエリは落とす
        var url = uri.GetLeftPart(UriPartial.Path);
        var target = uri.Host + uri.AbsolutePath;
        if (!Patterns.TryGetValue(site.Key, out var pattern))
            return (site.Key + ":" + target.TrimEnd('/').ToLowerInvariant(), url);

        var m = pattern.Match(target);
        if (!m.Success) return null;
        var key = m.Groups["id"].Success ? m.Groups["id"].Value : target.TrimEnd('/');
        return (site.Key + ":" + key.ToLowerInvariant(), url);
    }

    /// <summary>YouTube のサムネイル（ページの og:image は画面遷移で古いまま残ることがあるので、ID から作る）。</summary>
    public static string? YouTubeThumbnail(string key) =>
        key.StartsWith("youtube:", StringComparison.Ordinal) ? $"https://i.ytimg.com/vi/{key[8..]}/mqdefault.jpg" : null;

    /// <summary>どの作品でも同じ画像（サイトのロゴ）。サムネイルにならないので使わない。</summary>
    public static bool IsGenericImage(Uri image) =>
        image.AbsolutePath.Contains("/primevideo/seo/", StringComparison.OrdinalIgnoreCase);

    /// <summary>「タイトル - YouTube」「Prime Video: タイトル」のようなサイト名を取り除く。</summary>
    public static string CleanTitle(Site site, string? title)
    {
        // Netflix は文字の間に目に見えない文字（U+FEFF など）を混ぜているので取り除く
        title = Regex.Replace(title ?? "", "[​-‍⁠﻿]", "").Trim();
        // Prime Video は「Amazon.co.jp: 〇〇を観る」
        if (site.Key == "primevideo")
            title = Regex.Replace(title, @"^Amazon\.co\.jp\s*[:：]\s*(.+?)を観る$", "$1");
        var name = Regex.Escape(site.Name);
        // 「〇〇 | Disney+(ディズニープラス)」のように、サイト名のあとに括弧書きが付くこともある
        title = Regex.Replace(title, $@"\s*[-|｜–—:：]\s*{name}(\s*[(（][^)）]*[)）])?\s*$", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, $@"^{name}\s*[-|｜–—:：]\s*", "", RegexOptions.IgnoreCase);
        // サイト名だけのタイトル（Netflix の再生画面など）は、ないものとして扱う
        return title.Equals(site.Name, StringComparison.OrdinalIgnoreCase) ? "" : title.Trim();
    }
}
