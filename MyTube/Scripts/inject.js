// MyTube: YouTube のページに差し込むスクリプト。
// 動画一覧で視聴済みの動画を薄く表示し、「視聴済み」ラベルを付ける。
(() => {
  'use strict';
  if (window.top !== window) return;
  if (!/(^|\.)youtube\.com$/.test(location.hostname)) return;
  if (window.__mytube) return;
  window.__mytube = true;

  // 動画1件分のカード（YouTube の UI 変更に備えて新旧両方を並べている）
  const CONTAINERS = [
    'ytd-rich-item-renderer',
    'ytd-video-renderer',
    'ytd-grid-video-renderer',
    'ytd-compact-video-renderer',
    'ytd-playlist-video-renderer',
    'ytd-playlist-panel-video-renderer',
    'ytd-reel-item-renderer',
    'ytm-shorts-lockup-view-model-v2',
    'ytm-shorts-lockup-view-model',
    'yt-lockup-view-model',
  ].join(',');

  // YouTube 側の「途中まで見た」赤いバー
  const PROGRESS = [
    'ytd-thumbnail-overlay-resume-playback-renderer',
    'yt-thumbnail-overlay-progress-bar-view-model',
    '.ytThumbnailOverlayProgressBarHostWatchedProgressBarSegment',
  ].join(',');

  const host = window.chrome && window.chrome.webview;
  const post = (msg) => { if (host) host.postMessage(msg); };
  const watched = new Set();

  const style = document.createElement('style');
  style.textContent = `
    /* YouTube 側の左メニューはアプリのメニューと重複するので隠す */
    #guide-button, ytd-mini-guide-renderer, tp-yt-app-drawer#guide, #guide-wrapper { display: none !important; }
    ytd-app { --ytd-mini-guide-width: 0px !important; --app-drawer-width: 0px !important; }
    ytd-app ytd-page-manager { margin-left: 0 !important; }

    /* 上部バー（ロゴ・検索など）もアプリ側に持たせるので隠す */
    #masthead-container, ytd-masthead { display: none !important; }
    ytd-app { --ytd-masthead-height: 0px !important; }
    ytd-app ytd-page-manager { margin-top: 0 !important; }
    /* 分類ボタンの後ろの半透明の板は「上部バー＋分類ボタン」の高さで固定されているので、上部バーの分だけ上にずらす */
    ytd-app #frosted-glass { transform: translateY(-56px) !important; }

    html.mytube-dim [data-mytube-watched="1"] { opacity: .4; transition: opacity .15s; }
    html.mytube-dim [data-mytube-watched="1"]:hover { opacity: 1; }
    [data-mytube-id] { position: relative; }
    .mytube-toggle {
      position: absolute; top: 6px; left: 6px; z-index: 50;
      display: none; padding: 2px 8px; border: 0; border-radius: 4px;
      font: 500 12px/18px Roboto, "Yu Gothic UI", sans-serif;
      color: #fff; background: rgba(0,0,0,.8); cursor: pointer;
    }
    [data-mytube-id]:hover > .mytube-toggle { display: block; }
    [data-mytube-watched="1"] > .mytube-toggle { display: block; background: rgba(6,95,212,.9); }
    [data-mytube-state="yt"] > .mytube-toggle { cursor: default; }
    .mytube-toggle .hover { display: none; }
    .mytube-toggle:hover .hover { display: inline; }
    .mytube-toggle:hover .hover ~ .normal { display: none; }

    /* 登録チャンネル新着：「関連が強い」「ショート」などの棚をなくして新しい順の一覧だけにする */
    ytd-browse[page-subtype="subscriptions"] ytd-rich-section-renderer { display: none !important; }

    /* チャンネルのまとめ表示 */
    [data-mytube-hidden="1"] { display: none !important; }
    [data-mytube-chan-sig] { position: relative; }
    .mytube-chanbar {
      position: absolute; top: 6px; right: 6px; z-index: 50;
      display: flex; flex-direction: column; align-items: flex-end; gap: 4px;
    }
    .mytube-chanbar button {
      padding: 2px 8px; border: 0; border-radius: 4px; cursor: pointer;
      font: 500 12px/18px Roboto, "Yu Gothic UI", sans-serif;
    }
    .mytube-group { color: #0f0f0f; background: rgba(255,255,255,.92); }
    .mytube-group:hover { background: #fff; }
    .mytube-chan { display: none; color: #fff; background: rgba(0,0,0,.8); }
    [data-mytube-chan-sig]:hover .mytube-chan { display: block; }

    /* 動画ページ：右の列を「関連動画／コメント」のタブにして、右の列だけスクロールさせる */
    ytd-watch-flexy[mytube-split] #secondary {
      position: sticky; top: 0; align-self: flex-start;
      max-height: calc(100vh - 24px); overflow-y: auto; overscroll-behavior: contain;
      padding-top: 0 !important;
      scrollbar-width: thin; scrollbar-color: #555 transparent;
    }
    /* タブの帯：右の列の上端に固定し、不透明な背景で下を流れる文字を隠す */
    #mytube-side-tabs {
      position: sticky; top: 0; z-index: 5;
      display: flex; gap: 8px; margin: 0 0 12px; padding: 12px 0 10px;
      background: #0f0f0f;
      /* コメントは帯より右にはみ出すことがあるので、背景を右の列の端まで伸ばす（列の外は切れる） */
      box-shadow: 48px 0 0 #0f0f0f;
    }
    #mytube-side-tabs::after {
      content: ""; position: absolute; left: 0; right: -48px; bottom: 0; height: 1px; background: #272727;
    }
    #mytube-side-tabs button {
      padding: 6px 12px; border: 0; border-radius: 8px; cursor: pointer;
      font: 500 14px/20px Roboto, "Yu Gothic UI", sans-serif;
      color: #f1f1f1; background: #272727;
    }
    #mytube-side-tabs button:hover { background: #3f3f3f; }
    #mytube-side-tabs button[aria-selected="true"] { color: #0f0f0f; background: #f1f1f1; }
    ytd-watch-flexy:not([mytube-split]) #mytube-side-tabs,
    ytd-watch-flexy:not([mytube-split]) #mytube-comments-panel { display: none; }
    ytd-watch-flexy[mytube-split][data-mytube-tab="comments"] #secondary #related { display: none !important; }
    ytd-watch-flexy[mytube-split][data-mytube-tab="related"] #mytube-comments-panel { display: none; }
  `;

  function videoIdFromHref(href) {
    try {
      const u = new URL(href, location.origin);
      if (u.pathname === '/watch') return u.searchParams.get('v');
      const m = u.pathname.match(/^\/shorts\/([\w-]{11})/);
      return m ? m[1] : null;
    } catch {
      return null;
    }
  }

  function findId(el) {
    const a = el.querySelector('a[href*="/watch?v="], a[href^="/shorts/"]');
    return a ? videoIdFromHref(a.getAttribute('href')) : null;
  }

  function span(cls, text) {
    const s = document.createElement('span');
    s.className = cls;
    s.textContent = text;
    return s;
  }

  // innerHTML は YouTube の Trusted Types で弾かれるので DOM API で組み立てる
  const LABELS = {
    none:  [span('normal', '✓ 視聴済みにする')],
    local: [span('hover', '↺ 未視聴に戻す'), span('normal', '視聴済み')],
    yt:    [span('normal', '視聴履歴あり')],
  };

  function onToggle(e) {
    e.preventDefault();
    e.stopPropagation();
    const el = e.currentTarget.parentElement;
    const id = el && el.dataset.mytubeId;
    if (!id) return;
    if (el.dataset.mytubeState === 'local') {
      watched.delete(id);
      post({ type: 'unmark', id });
    } else if (el.dataset.mytubeState === 'none') {
      watched.add(id);
      post({ type: 'mark', id });
    }
    scan();
  }

  const stop = (e) => e.stopPropagation();

  function render(el, state) {
    let btn = el.querySelector(':scope > .mytube-toggle');
    if (!btn) {
      btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'mytube-toggle';
      btn.addEventListener('click', onToggle);
      for (const t of ['mousedown', 'mouseup', 'pointerdown', 'pointerup']) btn.addEventListener(t, stop);
      el.appendChild(btn);
    }
    btn.replaceChildren(...LABELS[state].map((n) => n.cloneNode(true)));
  }

  function scan() {
    timer = 0;
    for (const el of document.querySelectorAll(CONTAINERS)) {
      // 入れ子になっている場合は外側のカードだけ扱う
      if (el.parentElement && el.parentElement.closest(CONTAINERS)) continue;

      const id = findId(el);
      if (!id) continue;

      const state = watched.has(id) ? 'local' : el.querySelector(PROGRESS) ? 'yt' : 'none';
      const hasBtn = !!el.querySelector(':scope > .mytube-toggle');
      if (el.dataset.mytubeId === id && el.dataset.mytubeState === state && hasBtn) continue;

      el.dataset.mytubeId = id;
      el.dataset.mytubeState = state;
      el.dataset.mytubeWatched = state === 'none' ? '0' : '1';
      render(el, state);
    }
    applyGrouping();
    applyWatchLayout();
  }

  // ---- 動画ページ：コメントを右の列のタブに移す ----

  let sideTab = 'comments';
  let commentsHome = null; // コメントの元の場所（1列表示に戻すとき用）

  function applyWatchLayout() {
    const flexy = document.querySelector('ytd-watch-flexy');
    if (!flexy) return;
    const comments = flexy.querySelector('ytd-comments#comments');
    const secondary = flexy.querySelector('#secondary');
    const related = secondary && secondary.querySelector('#related');
    if (!comments || !related) return;

    const active = location.pathname === '/watch' &&
      flexy.hasAttribute('is-two-columns_') &&
      !flexy.hasAttribute('theater') &&
      !flexy.hasAttribute('fullscreen');

    let panel = flexy.querySelector('#mytube-comments-panel');

    if (!active) {
      if (flexy.hasAttribute('mytube-split')) flexy.removeAttribute('mytube-split');
      if (panel && comments.parentElement === panel && commentsHome && commentsHome.isConnected) {
        commentsHome.replaceWith(comments);
        commentsHome = null;
      }
      return;
    }

    let tabs = flexy.querySelector('#mytube-side-tabs');
    if (!tabs) {
      tabs = document.createElement('div');
      tabs.id = 'mytube-side-tabs';
      for (const [tab, label] of [['related', '関連動画'], ['comments', 'コメント']]) {
        const b = document.createElement('button');
        b.type = 'button';
        b.dataset.tab = tab;
        b.textContent = label;
        b.addEventListener('click', () => {
          sideTab = tab;
          secondary.scrollTop = 0;
          applyWatchLayout();
        });
        tabs.appendChild(b);
      }
      related.before(tabs);
      // YouTube はウィンドウのスクロールで続きを読み込むので、右の列のスクロールも伝える
      secondary.addEventListener('scroll', () => window.dispatchEvent(new Event('scroll')), { passive: true });
    }
    if (!panel) {
      panel = document.createElement('div');
      panel.id = 'mytube-comments-panel';
      related.before(panel);
    }
    if (comments.parentElement !== panel) {
      commentsHome = document.createComment('mytube-comments-home');
      comments.before(commentsHome);
      panel.appendChild(comments);
    }

    if (!flexy.hasAttribute('mytube-split')) flexy.setAttribute('mytube-split', '');
    if (flexy.dataset.mytubeTab !== sideTab) flexy.dataset.mytubeTab = sideTab;

    // タブにコメント数を出す
    const countEl = comments.querySelector('ytd-comments-header-renderer #count');
    const count = countEl ? (countEl.textContent.match(/[\d,]+/) || [''])[0] : '';
    for (const b of tabs.children) {
      const selected = String(b.dataset.tab === sideTab);
      if (b.getAttribute('aria-selected') !== selected) b.setAttribute('aria-selected', selected);
      if (b.dataset.tab === 'comments') {
        const label = count ? `コメント ${count}` : 'コメント';
        if (b.textContent !== label) b.textContent = label;
      }
    }
  }

  // ---- チャンネルのまとめ表示（登録チャンネル新着のみ） ----

  const collapsedChannels = new Set(); // まとめるチャンネル（アプリに保存）
  const expandedChannels = new Set();  // 「ほか N件」を押して一時的に広げているチャンネル

  function channelOf(el) {
    const links = el.querySelectorAll('a[href^="/@"], a[href^="/channel/"]');
    if (!links.length) return null;
    const parts = links[0].getAttribute('href').split(/[?#]/)[0].split('/');
    let key = parts.slice(0, parts[1] === 'channel' ? 3 : 2).join('/');
    try { key = decodeURIComponent(key); } catch { /* そのまま */ }
    let name = '';
    for (const a of links) {
      name = a.textContent.trim();
      if (name) break;
    }
    return { key, name: name || key.replace(/^\/@?/, '') };
  }

  function applyGrouping() {
    for (const grid of document.querySelectorAll('ytd-browse[page-subtype="subscriptions"] ytd-rich-grid-renderer > #contents')) {
      const items = [...grid.children]
        .filter((e) => e.tagName === 'YTD-RICH-ITEM-RENDERER')
        .map((item) => ({ item, ch: channelOf(item) }));

      // まとめるチャンネルごとに、最新（一番上）の1件と残りの件数を数える
      const firsts = new Map();
      const counts = new Map();
      for (const { item, ch } of items) {
        if (!ch || !collapsedChannels.has(ch.key)) continue;
        if (firsts.has(ch.key)) counts.set(ch.key, counts.get(ch.key) + 1);
        else { firsts.set(ch.key, item); counts.set(ch.key, 0); }
      }

      for (const { item, ch } of items) {
        const collapsed = !!ch && collapsedChannels.has(ch.key);
        const first = collapsed && firsts.get(ch.key) === item;
        const hide = collapsed && !first && !expandedChannels.has(ch.key);
        if ((item.dataset.mytubeHidden === '1') !== hide) {
          if (hide) item.dataset.mytubeHidden = '1';
          else delete item.dataset.mytubeHidden;
        }
        renderChanBar(item, ch, collapsed, first ? counts.get(ch.key) : 0);
      }
    }
  }

  function chanButton(cls, text, onClick) {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = cls;
    b.textContent = text;
    b.addEventListener('click', (e) => {
      e.preventDefault();
      e.stopPropagation();
      onClick();
      applyGrouping();
    });
    for (const t of ['mousedown', 'mouseup', 'pointerdown', 'pointerup']) b.addEventListener(t, stop);
    return b;
  }

  function renderChanBar(item, ch, collapsed, others) {
    const expanded = !!ch && expandedChannels.has(ch.key);
    const sig = ch ? `${ch.key}|${collapsed}|${others}|${expanded}` : '';
    let bar = item.querySelector(':scope > .mytube-chanbar');
    if (item.dataset.mytubeChanSig === sig && (bar || !ch)) return;

    item.dataset.mytubeChanSig = sig;
    if (!ch) { if (bar) bar.remove(); return; }
    if (!bar) {
      bar = document.createElement('div');
      bar.className = 'mytube-chanbar';
      item.appendChild(bar);
    }

    const buttons = [];
    if (collapsed && others > 0) {
      buttons.push(chanButton('mytube-group', expanded ? '▴ 畳む' : `${ch.name} ほか ${others}件 ▾`, () => {
        if (expandedChannels.has(ch.key)) expandedChannels.delete(ch.key);
        else expandedChannels.add(ch.key);
      }));
    }
    buttons.push(chanButton('mytube-chan', collapsed ? 'まとめを解除' : '≡ このチャンネルをまとめる', () => {
      const value = !collapsedChannels.has(ch.key);
      if (value) collapsedChannels.add(ch.key);
      else collapsedChannels.delete(ch.key);
      expandedChannels.delete(ch.key);
      post({ type: 'collapseChannel', key: ch.key, name: ch.name, value });
    }));
    bar.replaceChildren(...buttons);
  }

  let timer = 0;
  function schedule() {
    if (!timer) timer = setTimeout(scan, 200);
  }

  let dim = true;
  function setDim(on) {
    dim = !!on;
    if (document.documentElement) document.documentElement.classList.toggle('mytube-dim', dim);
  }

  if (host) {
    host.addEventListener('message', (e) => {
      const m = e.data || {};
      switch (m.type) {
        case 'init':
          watched.clear();
          (m.ids || []).forEach((id) => watched.add(id));
          collapsedChannels.clear();
          (m.collapsedChannels || []).forEach((k) => collapsedChannels.add(k));
          setDim(m.dim);
          break;
        case 'mark':
          watched.add(m.id);
          break;
        case 'unmark':
          watched.delete(m.id);
          break;
        case 'dim':
          setDim(m.value);
          break;
      }
      schedule();
    });
  }

  // このスクリプトはページの読み込み開始直後に動くので、DOM ができてから準備する
  function setup() {
    (document.head || document.documentElement).appendChild(style);
    setDim(dim);
    new MutationObserver(schedule).observe(document.documentElement, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ['href'],
    });
    schedule();
    const cfg = window.ytcfg && window.ytcfg.get;
    post({ type: 'ready', loggedIn: cfg ? !!window.ytcfg.get('LOGGED_IN') : null });
  }

  // 「/」キーでアプリ側の検索欄を開く（YouTube 標準のショートカットと同じキー）
  window.addEventListener('keydown', (e) => {
    if (e.key !== '/' || e.ctrlKey || e.altKey || e.metaKey) return;
    const t = e.target;
    if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    post({ type: 'openSearch' });
  }, true);

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', setup, { once: true });
  } else {
    setup();
  }
})();
