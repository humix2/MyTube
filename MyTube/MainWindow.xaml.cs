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
    private readonly string _injectScript = LoadInjectScript();
    private CoreWebView2Environment? _env;
    private bool _extensionsLoaded;

    // 分割画面。並び順がそのまま配置の順（0 番目が「大きい枠」）
    private readonly List<VideoPane> _panes = [];
    private VideoPane? _active;
    private PaneLayout _layout = PaneLayout.Single;
    private bool _layoutBusy;
    /// <summary>音を出している画面（null ならすべて消音）。選択中の画面とは別で、切り替えるまで変わらない。</summary>
    private VideoPane? _audioPane;

    /// <summary>選択中の画面。左メニュー・検索・戻る/進むはこの画面に効く。</summary>
    private CoreWebView2? ActiveCore => _active?.Core;

    // 全画面表示に入る前のウィンドウ状態
    private bool _isFullScreen;
    private VideoPane? _fullScreenPane;
    private WindowState _stateBeforeFullScreen;
    private Rect _boundsBeforeFullScreen;
    private Thickness _paneHostMargin;

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
        _paneHostMargin = PaneHost.Margin;
        RestoreWindowBounds();
        DimWatchedCheck.IsChecked = _settings.DimWatched;
        SetSidebarCollapsed(_settings.SidebarCollapsed, animate: false);

        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => UpdateWindowStateVisuals();
        Loaded += async (_, _) => await InitializeAsync();
        Closing += (_, _) => SaveSettings();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true };
            _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"), options);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"WebView2 を起動できませんでした。\n\n{ex.Message}", "MyTube",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        if (await CreatePaneAsync(_settings.StartUrl, activate: true) is null)
            Close();
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

    // ---- 分割画面 ----

    /// <summary>
    /// 画面を 1 つ追加して url を開く。どの画面も同じ WebView2 の環境を使うので、
    /// ログイン・Cookie・拡張機能（広告ブロック）は共通になる。
    /// </summary>
    private async Task<VideoPane?> CreatePaneAsync(string url, bool activate)
    {
        if (_env is null) return null;

        var pane = new VideoPane((Style)FindResource("ToolButton"));
        pane.ActivateRequested += (_, _) => SetActivePane(pane, focus: true);
        pane.CloseRequested += (_, _) => ClosePane(pane);
        pane.AudioRequested += (_, _) => SetAudioPane(_audioPane == pane ? null : pane);
        pane.MakeMainRequested += (_, _) => MovePaneToMain(pane);
        pane.MonitorFullScreenRequested += (_, _) => { if (pane.IsVideoFilled) SetFullScreen(pane); };
        pane.WebView.GotFocus += (_, _) => SetActivePane(pane, focus: false);
        _panes.Add(pane);
        PaneHost.Children.Add(pane);
        ApplyLayout();

        try
        {
            await pane.WebView.EnsureCoreWebView2Async(_env);
        }
        catch (Exception ex)
        {
            RemovePane(pane);
            ApplyLayout();
            MessageBox.Show(this, $"画面を開けませんでした。\n\n{ex.Message}", "MyTube",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        if (!_panes.Contains(pane)) return null; // 準備中に閉じられた

        var core = pane.WebView.CoreWebView2;
        pane.Attach(core);
        core.Settings.IsStatusBarEnabled = true;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false; // 配布用では開発者ツールを開けないようにする
#endif
        core.PermissionRequested += OnPermissionRequested;

        // 拡張機能はプロファイル（全画面で共通）に入るので、最初の 1 回だけ読み込む
        if (!_extensionsLoaded)
        {
            _extensionsLoaded = true;
            await LoadExtensionsAsync(core);
        }
        await core.AddScriptToExecuteOnDocumentCreatedAsync(_injectScript);

        core.WebMessageReceived += (_, e) => OnWebMessageReceived(pane, e);
        core.NewWindowRequested += (_, e) => OnNewWindowRequested(pane, e);
        core.NavigationStarting += OnNavigationStarting;
        core.ContextMenuRequested += (_, e) => OnContextMenuRequested(pane, e);
        core.SourceChanged += (_, _) => OnSourceChanged(pane);
        core.HistoryChanged += (_, _) => { if (pane == _active) UpdateNavButtons(); };
        core.DocumentTitleChanged += (_, _) =>
        {
            pane.SetTitle(core.DocumentTitle?.Replace(" - YouTube", "").Trim() ?? "");
            if (pane == _active) UpdateTitle();
        };
        // YouTube の全画面：1 画面のときはモニター全体に広げる。
        // 分割中はウィンドウを広げない。WebView2 は全画面の動画をその枠いっぱいに出すので、
        // 枠ごとに最大化した状態で並べられる（見出しのボタンでモニター全体にもできる）
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (core.ContainsFullScreenElement)
            {
                if (_panes.Count == 1) SetFullScreen(pane);
                else UpdatePaneChrome();
            }
            else
            {
                if (_fullScreenPane == pane) SetFullScreen(null);
                UpdatePaneChrome();
            }
        };

        if (activate || _active is null) SetActivePane(pane, focus: activate);
        ApplyAudio(); // 新しい画面は消音で始まる（音は今の画面のまま）
        core.Navigate(url);
        return pane;
    }

    private void RemovePane(VideoPane pane)
    {
        if (!_panes.Remove(pane)) return;
        if (_fullScreenPane == pane) SetFullScreen(null);
        PaneHost.Children.Remove(pane);
        pane.WebView.Dispose();
        if (_active == pane && _panes.Count > 0) SetActivePane(_panes[0], focus: false);
        if (_audioPane == pane) SetAudioPane(_active); // 音を出していた画面を閉じたら、選択中の画面の音にする
    }

    private void ClosePane(VideoPane pane)
    {
        if (_panes.Count <= 1) return;
        RemovePane(pane);
        _layout = PaneLayoutDef.DefaultFor(_panes.Count);
        ApplyLayout();
        PromoteSingleFullScreen();
    }

    /// <summary>1 画面に戻ったとき、枠いっぱいに最大化していた動画はいつも通りモニター全体に広げる。</summary>
    private void PromoteSingleFullScreen()
    {
        if (_panes.Count == 1 && _panes[0].IsVideoFilled && !_isFullScreen)
            SetFullScreen(_panes[0]);
    }

    /// <summary>「大1＋小N」の並べ方で、この画面を大きい枠（0 番目）に移す。</summary>
    private void MovePaneToMain(VideoPane pane)
    {
        var index = _panes.IndexOf(pane);
        if (index <= 0) return;
        (_panes[0], _panes[index]) = (_panes[index], _panes[0]);
        ApplyLayout();
        SetActivePane(pane, focus: true);
    }

    /// <summary>動画を新しい画面で開く。もう 4 画面あるときは元の画面で開く。</summary>
    private async void OpenInNewPane(string url, VideoPane source)
    {
        if (_panes.Count >= PaneLayoutDef.MaxPanes || _layoutBusy || _isFullScreen)
        {
            source.Core?.Navigate(url);
            return;
        }
        _layoutBusy = true;
        try
        {
            _layout = PaneLayoutDef.DefaultFor(_panes.Count + 1);
            await CreatePaneAsync(url, activate: true);
        }
        finally
        {
            _layoutBusy = false;
        }
    }

    private async Task SetLayoutAsync(PaneLayout layout)
    {
        if (_layoutBusy) return;
        _layoutBusy = true;
        try
        {
            var target = PaneLayoutDef.Get(layout).Count;
            // 減らすときは、選択中の画面を残して後ろから閉じる
            while (_panes.Count > target)
                RemovePane(_panes.Last(p => p != _active));

            _layout = layout;
            ApplyLayout();
            PromoteSingleFullScreen();

            while (_panes.Count < target)
            {
                if (await CreatePaneAsync(_settings.StartUrl, activate: false) is null) break;
            }
        }
        finally
        {
            _layoutBusy = false;
        }
    }

    /// <summary>並べ方に合わせて画面を置き直す。全画面中はその画面だけを出す。</summary>
    private void ApplyLayout()
    {
        PaneHost.RowDefinitions.Clear();
        PaneHost.ColumnDefinitions.Clear();

        if (_fullScreenPane is { } fs)
        {
            foreach (var p in _panes)
                p.Visibility = p == fs ? Visibility.Visible : Visibility.Collapsed;
            Place(fs, (0, 0, 1, 1));
            fs.SetChrome(split: false, active: true, canMakeMain: false);
            return;
        }

        // 1 画面のときは見出し（音声ボタン）が出ないので、必ず音を出す
        if (_panes.Count == 1 && _audioPane != _panes[0]) SetAudioPane(_panes[0]);

        var def = PaneLayoutDef.Get(_layout);
        foreach (var w in def.Columns)
            PaneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        foreach (var h in def.Rows)
            PaneHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h, GridUnitType.Star) });

        for (var i = 0; i < _panes.Count; i++)
        {
            var visible = i < def.Cells.Length;
            _panes[i].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) Place(_panes[i], def.Cells[i]);
        }
        UpdatePaneChrome();
    }

    private static void Place(UIElement pane, (int Row, int Column, int RowSpan, int ColumnSpan) cell)
    {
        Grid.SetRow(pane, cell.Row);
        Grid.SetColumn(pane, cell.Column);
        Grid.SetRowSpan(pane, cell.RowSpan);
        Grid.SetColumnSpan(pane, cell.ColumnSpan);
    }

    private void UpdatePaneChrome()
    {
        if (_fullScreenPane is not null) return;
        var split = _panes.Count > 1;
        var hasMain = PaneLayoutDef.Get(_layout).HasMain;
        for (var i = 0; i < _panes.Count; i++)
            _panes[i].SetChrome(split, active: _panes[i] == _active, canMakeMain: hasMain && i > 0);
    }

    private void SetActivePane(VideoPane pane, bool focus)
    {
        if (!_panes.Contains(pane)) return;
        if (_active != pane)
        {
            _active = pane;
            UpdatePaneChrome();
            UpdateTitle();
            UpdateNavButtons();
            UpdateAutoCollapse();
        }
        if (focus) pane.WebView.Focus();
    }

    /// <summary>音を出す画面を pane だけにする（null ならすべて消音）。</summary>
    private void SetAudioPane(VideoPane? pane)
    {
        _audioPane = pane;
        ApplyAudio();
    }

    private void ApplyAudio()
    {
        foreach (var p in _panes)
        {
            if (p.Core is { } core) core.IsMuted = p != _audioPane;
        }
    }

    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<Control>();
        foreach (var def in PaneLayoutDef.All)
        {
            var layout = def.Layout;
            items.Add(MakeMenuItem(def.Label, () => _ = SetLayoutAsync(layout), isChecked: layout == _layout));
        }
        OpenMenu((FrameworkElement)sender, PlacementMode.Bottom, items);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Ctrl+1〜4 で画面を選ぶ。選択中の画面の番号をもう一度押すと、その画面の音に切り替える
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            var index = e.Key switch
            {
                >= Key.D1 and <= Key.D4 => e.Key - Key.D1,
                >= Key.NumPad1 and <= Key.NumPad4 => e.Key - Key.NumPad1,
                _ => -1,
            };
            if (index >= 0 && index < _panes.Count && _panes[index].Visibility == Visibility.Visible)
            {
                if (_panes[index] == _active) SetAudioPane(_active);
                SetActivePane(_panes[index], focus: true);
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    // ---- ページとのやりとり ----

    // カメラ・マイク・通知・位置情報などはすべて断る。動画の自動再生だけは再生に関わるので許可する
    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e) =>
        e.State = e.PermissionKind == CoreWebView2PermissionKind.Autoplay
            ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Deny;

    /// <summary>メッセージの項目が文字列ならその値。それ以外（数値・なし・長すぎる）は null。</summary>
    private static string? GetString(JsonElement obj, string name, int maxLength = 200) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String &&
        p.GetString() is { } s && s.Length <= maxLength ? s : null;

    private static bool? GetBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? p.GetBoolean() : null;

    private void OnWebMessageReceived(VideoPane pane, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !YouTubeUrl.IsYouTube(source)) return;

        // ページから来るものなので、想定外の形でも落ちないように中身を確かめてから使う
        JsonDocument doc;
        try { doc = JsonDocument.Parse(e.WebMessageAsJson); }
        catch (JsonException) { return; }
        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        var id = GetString(root, "id");
        if (!YouTubeUrl.IsVideoId(id)) id = null;

        switch (GetString(root, "type"))
        {
            case "ready":
                Post(pane, new
                {
                    type = "init",
                    ids = _history.Ids,
                    dim = _settings.DimWatched,
                    chat = _settings.ShowChatComments,
                    collapsedChannels = _settings.CollapsedChannels.Keys,
                });
                if (GetBool(root, "loggedIn") is { } loggedIn)
                    SetLoggedIn(loggedIn);
                break;
            case "activate":
                SetActivePane(pane, focus: false);
                break;
            case "openSearch":
                SetActivePane(pane, focus: false);
                OpenSearch();
                break;
            case "collapseChannel":
                // チャンネルは "/@handle" か "/channel/ID" の形だけ受け付ける
                if (GetString(root, "key") is { } key &&
                    (key.StartsWith("/@", StringComparison.Ordinal) || key.StartsWith("/channel/", StringComparison.Ordinal)))
                {
                    if (GetBool(root, "value") == true)
                        _settings.CollapsedChannels[key] = GetString(root, "name") ?? key;
                    else
                        _settings.CollapsedChannels.Remove(key);
                    SaveSettings();
                }
                break;
            case "chatComments" when GetBool(root, "value") is { } on:
                _settings.ShowChatComments = on;
                SaveSettings();
                break;
            // 視聴済みはほかの画面の一覧にも反映する
            case "mark" when id is not null:
                _history.Add(id);
                PostAll(new { type = "mark", id }, except: pane);
                break;
            case "unmark" when id is not null:
                _history.Remove(id);
                PostAll(new { type = "unmark", id }, except: pane);
                break;
        }
    }

    private static void Post(VideoPane pane, object message) =>
        pane.Core?.PostWebMessageAsJson(JsonSerializer.Serialize(message));

    private void PostAll(object message, VideoPane? except = null)
    {
        var json = JsonSerializer.Serialize(message);
        foreach (var p in _panes)
        {
            if (p != except) p.Core?.PostWebMessageAsJson(json);
        }
    }

    private void OnSourceChanged(VideoPane pane)
    {
        // 動画を開いたら視聴済みとして記録
        var id = YouTubeUrl.GetVideoId(pane.Core?.Source);
        if (id is not null && _history.Add(id))
            PostAll(new { type = "mark", id });

        if (pane == _active)
        {
            UpdateNavButtons();
            UpdateAutoCollapse();
        }
    }

    /// <summary>選択中の画面で動画を見ている間は、左メニューを畳んで画面を広く使う。</summary>
    private void UpdateAutoCollapse()
    {
        if (!_settings.AutoCollapseOnWatch) return;
        var id = YouTubeUrl.GetVideoId(ActiveCore?.Source);
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

    private void UpdateTitle()
    {
        var page = _active?.PageTitle;
        Title = string.IsNullOrEmpty(page) ? "MyTube" : $"{page} - MyTube";
        TitleText.Text = string.IsNullOrEmpty(page) ? "MyTube" : page;
    }

    // ---- リンクの扱い ----

    private void OnNewWindowRequested(VideoPane pane, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;

        if (YouTubeUrl.GetRedirectTarget(uri) is { } target)
            OpenExternal(target);
        // 動画を中クリック・Ctrl+クリックしたら、分割して新しい画面で開く
        else if (YouTubeUrl.GetVideoId(e.Uri) is not null)
            OpenInNewPane(e.Uri, pane);
        else if (YouTubeUrl.IsYouTube(uri))
            pane.Core?.Navigate(e.Uri);
        else
            OpenExternal(e.Uri);
    }

    /// <summary>動画のリンクの右クリックメニューに「分割画面で開く」を足す。</summary>
    private void OnContextMenuRequested(VideoPane pane, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var target = e.ContextMenuTarget;
        if (_env is null || !target.HasLinkUri || _panes.Count >= PaneLayoutDef.MaxPanes || _isFullScreen) return;
        var url = target.LinkUri;
        if (YouTubeUrl.GetVideoId(url) is null) return;

        var item = _env.CreateContextMenuItem("分割画面で開く", null, CoreWebView2ContextMenuItemKind.Command);
        item.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => OpenInNewPane(url, pane));
        e.MenuItems.Insert(0, item);
        e.MenuItems.Insert(1, _env.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }

        // 説明欄などの外部リンク（youtube.com/redirect）は既定のブラウザで開く
        if (YouTubeUrl.GetRedirectTarget(uri) is { } target)
        {
            e.Cancel = true;
            OpenExternal(target);
        }
        // YouTube とログイン画面以外もアプリでは開かず、既定のブラウザに回す
        else if (!YouTubeUrl.IsAllowedInApp(uri))
        {
            e.Cancel = true;
            OpenExternal(e.Uri);
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
            ActiveCore?.Navigate(YouTubeOrigin + path);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ActiveCore?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => ActiveCore?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => ActiveCore?.Reload();

    private void UpdateNavButtons()
    {
        BackButton.IsEnabled = ActiveCore?.CanGoBack == true;
        ForwardButton.IsEnabled = ActiveCore?.CanGoForward == true;
    }

    private void DimWatched_Changed(object sender, RoutedEventArgs e)
    {
        _settings.DimWatched = DimWatchedCheck.IsChecked == true;
        PostAll(new { type = "dim", value = _settings.DimWatched });
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
        if (Uri.TryCreate(ActiveCore?.Source, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/results")
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
                ActiveCore?.Navigate($"{YouTubeOrigin}/results?search_query={Uri.EscapeDataString(query)}");
            CloseSearch();
            _active?.WebView.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearch();
            _active?.WebView.Focus();
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
            ActiveCore?.Navigate("https://accounts.google.com/ServiceLogin?service=youtube&continue=" +
                Uri.EscapeDataString($"{YouTubeOrigin}/signin?action_handle_signin=true&next=%2Ffeed%2Fsubscriptions"));
            return;
        }

        ShowMenu(AccountButton,
            ("アカウント設定", () => ActiveCore?.Navigate($"{YouTubeOrigin}/account")),
            ("ログアウト", () => ActiveCore?.Navigate($"{YouTubeOrigin}/logout")));
    }

    // ---- 拡張機能 ----

    private async void Extensions_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveCore is not { } core) return;
        var button = (Button)sender;

        var items = new List<(string, Action)>();
        foreach (var ext in await core.Profile.GetBrowserExtensionsAsync())
        {
            var folder = _settings.Extensions.FirstOrDefault(kv => kv.Value == ext.Id).Key;
            var page = folder is null ? null : GetOptionsPage(folder);
            if (page is not null)
                items.Add(($"{ext.Name} の設定", () => ActiveCore?.Navigate($"chrome-extension://{ext.Id}/{page}")));
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

    private static void ShowMenu(FrameworkElement target, params (string Label, Action Action)[] items) =>
        OpenMenu(target, PlacementMode.Right, items.Select(x => MakeMenuItem(x.Label, x.Action)));

    private static MenuItem MakeMenuItem(string label, Action action, bool isChecked = false)
    {
        var item = new MenuItem { Header = label, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    private static void OpenMenu(FrameworkElement target, PlacementMode placement, IEnumerable<Control> items)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
        foreach (var item in items) menu.Items.Add(item);
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
        PaneHost.Margin = maximized || _isFullScreen ? new Thickness(0) : _paneHostMargin;
    }

    /// <summary>pane を全画面にする（null で解除）。全画面中はその画面だけをモニター全体に出す。</summary>
    private void SetFullScreen(VideoPane? pane)
    {
        var on = pane is not null;
        if (on == _isFullScreen)
        {
            if (on && _fullScreenPane != pane)
            {
                _fullScreenPane = pane;
                ApplyLayout();
            }
            return;
        }

        if (on)
        {
            if (NativeMethods.GetMonitorRects(Hwnd) is not (var monitor, _)) return;
            _isFullScreen = true;
            _fullScreenPane = pane;
            _stateBeforeFullScreen = WindowState;
            _boundsBeforeFullScreen = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

            TitleBar.Visibility = Visibility.Collapsed;
            Sidebar.Visibility = Visibility.Collapsed;
            ApplyLayout();
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
            _fullScreenPane = null;
            ResizeMode = ResizeMode.CanResize;
            Left = _boundsBeforeFullScreen.Left;
            Top = _boundsBeforeFullScreen.Top;
            Width = _boundsBeforeFullScreen.Width;
            Height = _boundsBeforeFullScreen.Height;
            WindowState = _stateBeforeFullScreen;
            NativeMethods.SetRoundCorners(Hwnd, true);
            TitleBar.Visibility = Visibility.Visible;
            Sidebar.Visibility = Visibility.Visible;
            ApplyLayout();
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
