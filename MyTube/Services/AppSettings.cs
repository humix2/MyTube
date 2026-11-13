using System.Text.Json;

namespace MyTube.Services;

public sealed class AppSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 800;
    public bool Maximized { get; set; }

    /// <summary>視聴済みの動画を薄く表示する。</summary>
    public bool DimWatched { get; set; } = true;

    /// <summary>ライブ配信のチャットを動画の上に流す。</summary>
    public bool ShowChatComments { get; set; } = true;

    /// <summary>分割画面で、音は選択中の画面だけ出す（ほかの画面は消音）。</summary>
    public bool AudioFollowsActive { get; set; } = true;

    public string StartUrl { get; set; } = "https://www.youtube.com/feed/subscriptions";

    /// <summary>登録チャンネル新着でまとめて表示するチャンネル（"/@handle" → 表示名）。</summary>
    public Dictionary<string, string> CollapsedChannels { get; set; } = [];

    /// <summary>左メニューを畳んでいる。</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>動画を開いたら左メニューを自動で畳む。</summary>
    public bool AutoCollapseOnWatch { get; set; } = true;

    /// <summary>読み込み済みの拡張機能（フォルダ → 拡張機能ID）。毎回追加し直さないため。</summary>
    public Dictionary<string, string> Extensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception)
        {
            // 壊れていたら既定値
        }
        return new();
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}
