namespace MyTube;

/// <summary>分割画面の並べ方。</summary>
public enum PaneLayout
{
    Single,
    Columns2,
    Rows2,
    Main2,
    Grid4,
    Main3,
}

/// <summary>並べ方ごとの行・列の幅と、各画面を置くマス。1 つ目の画面が「大きい枠」。</summary>
public sealed record PaneLayoutDef(
    PaneLayout Layout,
    string Label,
    double[] Columns,
    double[] Rows,
    (int Row, int Column, int RowSpan, int ColumnSpan)[] Cells,
    bool HasMain)
{
    public int Count => Cells.Length;

    public const int MaxPanes = 4;

    public static readonly PaneLayoutDef[] All =
    [
        new(PaneLayout.Single, "1画面", [1], [1], [(0, 0, 1, 1)], false),
        new(PaneLayout.Columns2, "左右に2分割", [1, 1], [1], [(0, 0, 1, 1), (0, 1, 1, 1)], false),
        new(PaneLayout.Rows2, "上下に2分割", [1], [1, 1], [(0, 0, 1, 1), (1, 0, 1, 1)], false),
        new(PaneLayout.Main2, "大1＋小2", [2, 1], [1, 1], [(0, 0, 2, 1), (0, 1, 1, 1), (1, 1, 1, 1)], true),
        new(PaneLayout.Grid4, "4分割", [1, 1], [1, 1], [(0, 0, 1, 1), (0, 1, 1, 1), (1, 0, 1, 1), (1, 1, 1, 1)], false),
        new(PaneLayout.Main3, "大1＋小3", [3, 1], [1, 1, 1],
            [(0, 0, 3, 1), (0, 1, 1, 1), (1, 1, 1, 1), (2, 1, 1, 1)], true),
    ];

    public static PaneLayoutDef Get(PaneLayout layout) => All.First(x => x.Layout == layout);

    /// <summary>画面の数が変わったときに使う並べ方。</summary>
    public static PaneLayout DefaultFor(int count) => count switch
    {
        <= 1 => PaneLayout.Single,
        2 => PaneLayout.Columns2,
        3 => PaneLayout.Main2,
        _ => PaneLayout.Grid4,
    };
}
