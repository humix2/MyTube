using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using MyTube.Services;

namespace MyTube;

/// <summary>
/// 小窓（ピクチャインピクチャ）。画面（VideoPane）を WebView2 ごとこのウィンドウへ移すので、
/// 読み込み直さずに再生が続く。常に手前に出し、ツールウィンドウにしてあるので
/// 仮想デスクトップを切り替えても表示されたままになる（タスクバーと Alt+Tab には出ない）。
/// </summary>
public sealed class PipWindow : Window
{
    /// <summary>動画を枠いっぱいに出す。YouTube は自前の全画面ボタン、それ以外は一番大きい動画の入れ物を全画面にする。</summary>
    public const string FillScript = """
        (() => {
          if (document.fullscreenElement) return;
          const yt = document.querySelector('#movie_player .ytp-fullscreen-button');
          if (yt) { yt.click(); return; }
          const area = e => e.offsetWidth * e.offsetHeight;
          const videos = [...document.querySelectorAll('video')].filter(v => area(v) > 0);
          if (videos.length === 0) return;
          const video = videos.reduce((a, b) => area(a) >= area(b) ? a : b);
          // 操作ボタンも一緒に出るよう、動画とほぼ同じ大きさの入れ物まで上がる
          let el = video;
          while (el.parentElement && el.parentElement !== document.body &&
                 area(el.parentElement) <= area(video) * 1.3) el = el.parentElement;
          el.requestFullscreen().catch(() => {});
        })();
        """;

    public const string ExitFillScript = "if (document.fullscreenElement) document.exitFullscreen().catch(() => {});";

    private static readonly Brush TitleText = Frozen(0xAA, 0xAA, 0xAA);
    private static readonly Brush AudioOn = Frozen(0x3E, 0xA6, 0xFF);

    private readonly Border _holder;
    private readonly TextBlock _title;
    private readonly Button _muteButton;
    private readonly Button _fillButton;
    private readonly CoreWebView2 _core;

    public VideoPane Pane { get; }

    /// <summary>「元の画面に戻す」を押した。</summary>
    public event EventHandler? ReturnRequested;
    /// <summary>音声ボタンを押した。</summary>
    public event EventHandler? AudioRequested;

    public PipWindow(VideoPane pane, CoreWebView2 core, Style buttonStyle, Rect bounds)
    {
        Pane = pane;
        _core = core;

        Title = "MyTube 小窓";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = true;
        Background = Frozen(0x0F, 0x0F, 0x0F);
        MinWidth = 240;
        MinHeight = 160;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        // 見出しの空いている所をドラッグで移動。WebView2 の上ではサイズ変更できないので、周りを少し空ける
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 28,
            ResizeBorderThickness = new Thickness(5),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        _title = new TextBlock
        {
            Margin = new Thickness(10, 0, 6, 0),
            FontSize = 12,
            Foreground = TitleText,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _muteButton = MakeButton(buttonStyle, "", "", (_, _) => AudioRequested?.Invoke(this, EventArgs.Empty));
        _fillButton = MakeButton(buttonStyle, "", "", (_, _) =>
            _ = _core.ExecuteScriptAsync(_core.ContainsFullScreenElement ? ExitFillScript : FillScript));
        var returnButton = MakeButton(buttonStyle, "", "元の画面に戻す",
            (_, _) => ReturnRequested?.Invoke(this, EventArgs.Empty));
        var closeButton = MakeButton(buttonStyle, "", "閉じる（この動画も閉じる）", (_, _) => Close());
        closeButton.FontSize = 10;

        var header = new Grid { Height = 28 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_title);
        AddAt(header, _muteButton, 1);
        AddAt(header, _fillButton, 2);
        AddAt(header, returnButton, 3);
        AddAt(header, closeButton, 4);
        DockPanel.SetDock(header, Dock.Top);

        _holder = new Border { Margin = new Thickness(5, 0, 5, 5), Child = pane };
        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(header);
        root.Children.Add(_holder);
        Content = root;

        core.DocumentTitleChanged += OnTitleChanged;
        core.IsMutedChanged += OnMutedChanged;
        core.ContainsFullScreenElementChanged += OnFillChanged;
        OnTitleChanged(null, null);
        OnMutedChanged(null, null);
        OnFillChanged(null, null);

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetToolWindow(hwnd);
            NativeMethods.SetRoundCorners(hwnd, true);
            NativeMethods.SetBorderColor(hwnd, 0x3F, 0x3F, 0x3F);
        };
    }

    /// <summary>画面をこのウィンドウから外して返す（元のウィンドウへ戻す前に呼ぶ）。</summary>
    public VideoPane DetachPane()
    {
        _core.DocumentTitleChanged -= OnTitleChanged;
        _core.IsMutedChanged -= OnMutedChanged;
        _core.ContainsFullScreenElementChanged -= OnFillChanged;
        _holder.Child = null;
        return Pane;
    }

    private void OnTitleChanged(object? sender, object? e)
    {
        var title = _core.DocumentTitle?.Replace(" - YouTube", "").Trim();
        _title.Text = string.IsNullOrEmpty(title) ? "MyTube" : title;
        _title.ToolTip = _title.Text;
    }

    private void OnMutedChanged(object? sender, object? e)
    {
        var muted = _core.IsMuted;
        _muteButton.Content = muted ? "" : "";
        _muteButton.ToolTip = muted ? "この動画の音を出す" : "音を出している画面（押すとすべて消音）";
        _muteButton.Foreground = muted ? TitleText : AudioOn;
    }

    private void OnFillChanged(object? sender, object? e)
    {
        var filled = _core.ContainsFullScreenElement;
        _fillButton.Content = filled ? "" : "";
        _fillButton.ToolTip = filled ? "ページ全体を表示" : "動画だけを表示";
    }

    private static Button MakeButton(Style style, string icon, string tip, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Style = style,
            Content = icon,
            ToolTip = tip,
            FontSize = 12,
            Padding = new Thickness(9, 4, 9, 4),
            Focusable = false,
        };
        b.Click += onClick;
        return b;
    }

    private static void AddAt(Grid grid, UIElement child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
