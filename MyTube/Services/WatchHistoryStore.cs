using System.Text.Json;

namespace MyTube.Services;

/// <summary>アプリで開いた動画のIDを記録する（視聴済み表示に使う）。</summary>
public sealed class WatchHistoryStore
{
    private readonly string _path;
    private readonly Dictionary<string, DateTime> _items;

    public WatchHistoryStore(string path)
    {
        _path = path;
        _items = Load(path);
    }

    public IReadOnlyCollection<string> Ids => _items.Keys;

    /// <summary>追加する。新しく追加されたときだけ true。</summary>
    public bool Add(string videoId)
    {
        if (!_items.TryAdd(videoId, DateTime.UtcNow)) return false;
        Save();
        return true;
    }

    public bool Remove(string videoId)
    {
        if (!_items.Remove(videoId)) return false;
        Save();
        return true;
    }

    private static Dictionary<string, DateTime> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json) ?? [];
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
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items));
        File.Move(tmp, _path, overwrite: true);
    }
}
