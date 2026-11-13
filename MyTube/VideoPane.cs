using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MyTube;

/// <summary>
/// 分割画面の 1 画面分。上に細い見出し（タイトル・消音・大きい枠へ・閉じる）、その下に WebView2。
/// WebView2 の上には WPF の部品を重ねられないので、枠と見出しは WebView2 の外側に置く。
/// </summary>
public sealed class VideoPane : Border
{
    private static readonly Brush ActiveBorder = Frozen(0x3E, 0xA6, 0xFF);
    private static readonly Brush InactiveBorder = Frozen(0x2B, 0x2B, 0x2B);
    private static readonly Brush ActiveText = Frozen(0xF1, 0xF1, 0xF1);
    private static readonly Brush InactiveText = Frozen(0x90, 0x90, 0x90);

    private readonly Border _header;
    private readonly TextBlock _title;
    private readonly Button _muteButton;
    private readonly Button _mainButton;
    private readonly Button _closeButton;

    public WebView2 WebView { get; }
    public CoreWebView2? Core { get; private set; }
    public string PageTitle { get; private set; } = "";

    /// <summary>見出しをクリックした。</summary>
    public event EventHandler? ActivateRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? MakeMainRequested;

    public VideoPane(Style buttonStyle)
    {
        WebView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0F, 0x0F, 0x0F) };

        _title = new TextBlock
        {
            Margin = new Thickness(10, 0, 6, 0),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _muteButton = MakeButton(buttonStyle, "", "消音", (_, _) => ToggleMute());
        _mainButton = MakeButton(buttonStyle, "", "大きい枠に移す", (_, _) => MakeMainRequested?.Invoke(this, EventArgs.Empty));
        _closeButton = MakeButton(buttonStyle, "", "この画面を閉じる", (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty));
        _closeButton.FontSize = 10;

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.Children.Add(_title);
        AddAt(headerGrid, _muteButton, 1);
        AddAt(headerGrid, _mainButton, 2);
        AddAt(headerGrid, _closeButton, 3);

        _header = new Border { Height = 28, Background = Brushes.Transparent, Child = headerGrid };
        // ボタンはクリックを自分で受け取るので、ここに来るのは見出しの空いている所を押したときだけ
        _header.MouseLeftButtonDown += (_, _) => ActivateRequested?.Invoke(this, EventArgs.Empty);
        DockPanel.SetDock(_header, Dock.Top);

        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(_header);
        dock.Children.Add(WebView);

        Child = dock;
        Margin = new Thickness(2);
        BorderThickness = new Thickness(1);
        BorderBrush = InactiveBorder;
        SetChrome(split: false, active: true, canMakeMain: false);
    }

    public void Attach(CoreWebView2 core)
    {
        Core = core;
        core.IsMutedChanged += (_, _) => UpdateMuteButton();
        UpdateMuteButton();
    }

    public void SetTitle(string title)
    {
        PageTitle = title;
        _title.Text = string.IsNullOrEmpty(title) ? "MyTube" : title;
        _title.ToolTip = _title.Text;
    }

    /// <summary>
    /// 枠と見出しの見た目を変える。1 画面のときと全画面のときは見出しも枠も出さない（今までと同じ見た目）。
    /// </summary>
    public void SetChrome(bool split, bool active, bool canMakeMain)
    {
        _header.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        BorderThickness = new Thickness(split ? 1 : 0);
        Margin = new Thickness(split ? 2 : 0);
        BorderBrush = active ? ActiveBorder : InactiveBorder;
        _title.Foreground = active ? ActiveText : InactiveText;
        _mainButton.Visibility = canMakeMain ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleMute()
    {
        if (Core is not null) Core.IsMuted = !Core.IsMuted;
    }

    private void UpdateMuteButton()
    {
        var muted = Core?.IsMuted == true;
        _muteButton.Content = muted ? "" : "";
        _muteButton.ToolTip = muted ? "消音を解除" : "消音";
        _muteButton.Opacity = muted ? 0.6 : 1;
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
