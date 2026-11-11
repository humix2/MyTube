using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MyTube.Services;

namespace MyTube;

public partial class MainWindow : Window
{
    private const string YouTubeOrigin = "https://www.youtube.com";

    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyTube");

    private static string SettingsPath => Path.Combine(DataDir, "settings.json");
    private static string ExtensionsDir => Path.Combine(DataDir, "Extensions");

    private readonly AppSettings _settings;
    private readonly WatchHistoryStore _history;
    private CoreWebView2? _core;

    // 全画面表示に入る前のウィンドウ状態
    private bool _isFullScreen;
    private WindowState _stateBeforeFullScreen;
    private Rect _boundsBeforeFullScreen;
    private Thickness _webViewMargin;

    // 左メニュー
    private const double SidebarExpandedWidth = 200;
    private const double SidebarCollapsedWidth = 56;
    private bool _sidebarCollapsed;
    private bool _autoCollapsed; // 動画を開いたので自動で畳んだ

    private bool? _loggedIn;

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    public MainWindow()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ExtensionsDir);
        _settings = AppSettings.Load(SettingsPath);
        _history = new WatchHistoryStore(Path.Combine(DataDir, "watched.json"));

        InitializeComponent();
        _webViewMargin = WebView.Margin;
        RestoreWindowBounds();
        DimWatchedCheck.IsChecked = _settings.DimWatched;
        SetSidebarCollapsed(_settings.SidebarCollapsed, animate: false);

        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => UpdateWindowStateVisuals();
        Loaded += async (_, _) => await InitializeWebViewAsync();
        Closing += (_, _) => SaveSettings();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0F, 0x0F, 0x0F);

            var options = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true };
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"), options);
            await WebView.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"WebView2 を起動できませんでした。\n\n{ex.Message}", "MyTube",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        var core = _core = WebView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.AreDevToolsEnabled = true;

        await LoadExtensionsAsync(core);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(LoadInjectScript());

        core.WebMessageReceived += OnWebMessageReceived;
        core.NewWindowRequested += OnNewWindowRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.SourceChanged += (_, _) => OnSourceChanged();
        core.HistoryChanged += (_, _) => UpdateNavButtons();
        core.DocumentTitleChanged += (_, _) =>
        {
            var page = core.DocumentTitle?.Replace(" - YouTube", "").Trim();
            Title = string.IsNullOrEmpty(page) ? "MyTube" : $"{page} - MyTube";
            TitleText.Text = string.IsNullOrEmpty(page) ? "MyTube" : page;
        };
        core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);

        core.Navigate(_settings.StartUrl);
    }

    private static string LoadInjectScript()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("MyTube.inject.js")
            ?? throw new InvalidOperationException("inject.js が見つかりません");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>データフォルダの Extensions 以下にある展開済み拡張機能を読み込む。</summary>
    private async Task LoadExtensionsAsync(CoreWebView2 core)
    {
        var installedIds = (await core.Profile.GetBrowserExtensionsAsync()).Select(x => x.Id).ToHashSet();
        var folders = Directory.GetDirectories(ExtensionsDir)
            .Where(d => File.Exists(Path.Combine(d, "manifest.json")));

        var failed = new List<string>();
        foreach (var folder in folders)
        {
            if (_settings.Extensions.TryGetValue(folder, out var knownId) && installedIds.Contains(knownId))
                continue;
            try
            {
                var ext = await core.Profile.AddBrowserExtensionAsync(folder);
                _settings.Extensions[folder] = ext.Id;
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(folder)}: {ex.Message}");
            }
        }
        SaveSettings();

        if (failed.Count > 0)
        {
            MessageBox.Show(this, "拡張機能を読み込めませんでした。\n\n" + string.Join("\n", failed), "MyTube",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- ページとのやりとり ----

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !YouTubeUrl.IsYouTube(source)) return;

        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProp)) return;
        var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

        switch (typeProp.GetString())
        {
            case "ready":
                Post(new
                {
                    type = "init",
                    ids = _history.Ids,
                    dim = _settings.DimWatched,
                    chat = _settings.ShowChatComments,
                    collapsedChannels = _settings.CollapsedChannels.Keys,
                });
                if (root.TryGetProperty("loggedIn", out var li) && li.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    SetLoggedIn(li.GetBoolean());
                break;
            case "openSearch":
                OpenSearch();
                break;
            case "collapseChannel":
                if (root.TryGetProperty("key", out var keyProp) && keyProp.GetString() is { Length: > 0 } key)
                {
                    if (root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True)
                        _settings.CollapsedChannels[key] = root.TryGetProperty("name", out var n) ? n.GetString() ?? key : key;
                    else
                        _settings.CollapsedChannels.Remove(key);
                    SaveSettings();
                }
                break;
            case "chatComments" when root.TryGetProperty("value", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False:
                _settings.ShowChatComments = on.GetBoolean();
                SaveSettings();
                break;
            case "mark" when id is not null:
                _history.Add(id);
                break;
            case "unmark" when id is not null:
                _history.Remove(id);
                break;
        }
    }

    private void Post(object message) =>
        _core?.PostWebMessageAsJson(JsonSerializer.Serialize(message));

    private void OnSourceChanged()
    {
        UpdateNavButtons();

        // 動画を開いたら視聴済みとして記録
        var id = YouTubeUrl.GetVideoId(_core?.Source);
        if (id is not null && _history.Add(id))
            Post(new { type = "mark", id });

        // 動画を見ている間は左メニューを畳んで画面を広く使う
        if (_settings.AutoCollapseOnWatch)
        {
            if (id is not null && !_sidebarCollapsed)
            {
                SetSidebarCollapsed(true, animate: true);
                _autoCollapsed = true;
            }
            else if (id is null && _autoCollapsed)
            {
                _autoCollapsed = false;
                SetSidebarCollapsed(false, animate: true);
            }
        }
    }

    // ---- リンクの扱い ----

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;

        if (YouTubeUrl.GetRedirectTarget(uri) is { } target)
            OpenExternal(target);
        else if (YouTubeUrl.IsYouTube(uri))
            _core?.Navigate(e.Uri);
        else
            OpenExternal(e.Uri);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // 説明欄などの外部リンク（youtube.com/redirect）は既定のブラウザで開く
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && YouTubeUrl.GetRedirectTarget(uri) is { } target)
        {
            e.Cancel = true;
            OpenExternal(target);
        }
    }

    private static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    // ---- サイドバー ----

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
            _core?.Navigate(YouTubeOrigin + path);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _core?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _core?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => _core?.Reload();

    private void UpdateNavButtons()
    {
        BackButton.IsEnabled = _core?.CanGoBack == true;
        ForwardButton.IsEnabled = _core?.CanGoForward == true;
    }

    private void DimWatched_Changed(object sender, RoutedEventArgs e)
    {
        _settings.DimWatched = DimWatchedCheck.IsChecked == true;
        Post(new { type = "dim", value = _settings.DimWatched });
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(DataDir) { UseShellExecute = true });

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _autoCollapsed = false;
        SetSidebarCollapsed(!_sidebarCollapsed, animate: true);
        _settings.SidebarCollapsed = _sidebarCollapsed;
    }

    private void SetSidebarCollapsed(bool collapsed, bool animate)
    {
        _sidebarCollapsed = collapsed;
        var to = collapsed ? SidebarCollapsedWidth : SidebarExpandedWidth;

        // 畳むときは先に文字を消し、開くときは広がりきってから文字を出す
        if (collapsed) Resources["LabelVisibility"] = Visibility.Collapsed;

        if (!animate)
        {
            Sidebar.BeginAnimation(WidthProperty, null);
            Sidebar.Width = to;
            if (!collapsed) Resources["LabelVisibility"] = Visibility.Visible;
            return;
        }

        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (!collapsed)
            anim.Completed += (_, _) => { if (!_sidebarCollapsed) Resources["LabelVisibility"] = Visibility.Visible; };
        Sidebar.BeginAnimation(WidthProperty, anim);
    }

    // ---- 検索 ----

    private void OpenSearch_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch()
    {
        // 検索結果ページにいるときは今の検索語を入れておく
        if (Uri.TryCreate(_core?.Source, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/results")
            SearchBox.Text = YouTubeUrl.GetQuery(uri, "search_query") ?? SearchBox.Text;

        TitleText.Visibility = Visibility.Hidden;
        SearchPanel.Visibility = Visibility.Visible;

        // WebView2 からフォーカスを取り戻すため、少し遅らせてフォーカスする
        Dispatcher.BeginInvoke(() =>
        {
            Activate();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void CloseSearch()
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var query = SearchBox.Text.Trim();
            if (query.Length > 0)
                _core?.Navigate($"{YouTubeOrigin}/results?search_query={Uri.EscapeDataString(query)}");
            CloseSearch();
            WebView.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearch();
            WebView.Focus();
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;

    private void SearchBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CloseSearch();

    // ---- アカウント ----

    private void SetLoggedIn(bool loggedIn)
    {
        _loggedIn = loggedIn;
        AccountLabel.Text = loggedIn ? "アカウント" : "ログイン";
        AccountButton.ToolTip = AccountLabel.Text;
    }

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (_loggedIn == false)
        {
            _core?.Navigate("https://accounts.google.com/ServiceLogin?service=youtube&continue=" +
                Uri.EscapeDataString($"{YouTubeOrigin}/signin?action_handle_signin=true&next=%2Ffeed%2Fsubscriptions"));
            return;
        }

        ShowMenu(AccountButton,
            ("アカウント設定", () => _core?.Navigate($"{YouTubeOrigin}/account")),
            ("ログアウト", () => _core?.Navigate($"{YouTubeOrigin}/logout")));
    }

    // ---- 拡張機能 ----

    private async void Extensions_Click(object sender, RoutedEventArgs e)
    {
        if (_core is null) return;
        var button = (Button)sender;

        var items = new List<(string, Action)>();
        foreach (var ext in await _core.Profile.GetBrowserExtensionsAsync())
        {
            var folder = _settings.Extensions.FirstOrDefault(kv => kv.Value == ext.Id).Key;
            var page = folder is null ? null : GetOptionsPage(folder);
            if (page is not null)
                items.Add(($"{ext.Name} の設定", () => _core.Navigate($"chrome-extension://{ext.Id}/{page}")));
        }
        items.Add(("拡張機能フォルダを開く", () =>
            Process.Start(new ProcessStartInfo(ExtensionsDir) { UseShellExecute = true })));

        ShowMenu(button, [.. items]);
    }

    /// <summary>manifest.json から設定ページのパスを取り出す。</summary>
    private static string? GetOptionsPage(string folder)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            var root = doc.RootElement;
            if (root.TryGetProperty("options_ui", out var ui) && ui.TryGetProperty("page", out var p))
                return p.GetString()?.TrimStart('/');
            if (root.TryGetProperty("options_page", out var op))
                return op.GetString()?.TrimStart('/');
        }
        catch (Exception)
        {
            // manifest が読めなければ設定ページなし
        }
        return null;
    }

    private static void ShowMenu(FrameworkElement target, params (string Label, Action Action)[] items)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Right };
        foreach (var (label, action) in items)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---- ウィンドウ ----

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        NativeMethods.SetRoundCorners(Hwnd, true);
        NativeMethods.SetBorderColor(Hwnd, 0x2B, 0x2B, 0x2B);
        HwndSource.FromHwnd(Hwnd)?.AddHook(WndProc);
        UpdateWindowStateVisuals();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 枠なしウィンドウは最大化するとタスクバーに重なるので、作業領域に収める
        if (msg == NativeMethods.WM_GETMINMAXINFO && NativeMethods.GetMonitorRects(hwnd) is (var monitor, var work))
        {
            var info = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
            info.ptMaxPosition.X = work.Left - monitor.Left;
            info.ptMaxPosition.Y = work.Top - monitor.Top;
            info.ptMaxSize.X = work.Width;
            info.ptMaxSize.Y = work.Height;
            Marshal.StructureToPtr(info, lParam, false);
        }
        return IntPtr.Zero;
    }

    private void UpdateWindowStateVisuals()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "元に戻す" : "最大化";
        // 最大化中と全画面中はサイズ変更用のすき間が不要
        WebView.Margin = maximized || _isFullScreen ? new Thickness(0) : _webViewMargin;
    }

    private void SetFullScreen(bool on)
    {
        if (on == _isFullScreen) return;

        if (on)
        {
            if (NativeMethods.GetMonitorRects(Hwnd) is not (var monitor, _)) return;
            _isFullScreen = true;
            _stateBeforeFullScreen = WindowState;
            _boundsBeforeFullScreen = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

            TitleBar.Visibility = Visibility.Collapsed;
            Sidebar.Visibility = Visibility.Collapsed;
            NativeMethods.SetRoundCorners(Hwnd, false);
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;

            // モニター全体を覆う（物理ピクセル → WPF の単位に変換）
            var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
            var topLeft = toDip.Transform(new Point(monitor.Left, monitor.Top));
            var size = toDip.Transform(new Vector(monitor.Width, monitor.Height));
            Left = topLeft.X;
            Top = topLeft.Y;
            Width = size.X;
            Height = size.Y;
        }
        else
        {
            _isFullScreen = false;
            ResizeMode = ResizeMode.CanResize;
            Left = _boundsBeforeFullScreen.Left;
            Top = _boundsBeforeFullScreen.Top;
            Width = _boundsBeforeFullScreen.Width;
            Height = _boundsBeforeFullScreen.Height;
            WindowState = _stateBeforeFullScreen;
            NativeMethods.SetRoundCorners(Hwnd, true);
            TitleBar.Visibility = Visibility.Visible;
            Sidebar.Visibility = Visibility.Visible;
        }
        UpdateWindowStateVisuals();
    }

    private void RestoreWindowBounds()
    {
        Width = _settings.Width;
        Height = _settings.Height;
        if (_settings.Left is { } left && _settings.Top is { } top &&
            left >= SystemParameters.VirtualScreenLeft &&
            top >= SystemParameters.VirtualScreenTop &&
            left + 100 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            top + 100 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    private void SaveSettings()
    {
        var fullScreen = _isFullScreen;
        var bounds = fullScreen ? _boundsBeforeFullScreen
            : WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            _settings.Left = bounds.Left;
            _settings.Top = bounds.Top;
            _settings.Width = bounds.Width;
            _settings.Height = bounds.Height;
        }
        _settings.Maximized = fullScreen
            ? _stateBeforeFullScreen == WindowState.Maximized
            : WindowState == WindowState.Maximized;

        try { _settings.Save(SettingsPath); }
        catch (Exception) { /* 保存できなくても終了は止めない */ }
    }
}
