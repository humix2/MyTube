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
    /* 動画の右下に出るチャンネルのアイコン（ブランディング透かし）は不要なので隠す */
    .html5-video-player :is(.iv-branding, .branding-img-container, .ytp-branding-watermark, .annotation-type-custom.iv-branding) {
      display: none !important;
    }
    /* 読み込み中に出る仮の画面（スケルトン）の上部バーと左メニューも隠す */
    #masthead-skeleton-icons, .masthead_shell, #guide-skeleton { display: none !important; }
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
    /* 履歴ページは全部見た動画なので、薄く表示しない。ラベルもマウスを乗せたときだけ出す */
    html.mytube-dim ytd-browse[page-subtype="history"] [data-mytube-watched="1"] { opacity: 1; }
    ytd-browse[page-subtype="history"] [data-mytube-watched="1"]:not(:hover) > .mytube-toggle { display: none; }
    /* 履歴ページで「動画」の絞り込みに切り替わるまで、一覧を隠しておく（絞り込みのボタンは見せる） */
    html.mytube-history-loading ytd-browse[page-subtype="history"] :is(ytd-item-section-renderer, ytd-reel-shelf-renderer, ytd-continuation-item-renderer) {
      visibility: hidden !important;
    }
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
    /* 右の列は「タブの帯（固定）」と「その下のスクロールする部分」に分ける。
       帯をスクロールの外に置くので、帯に背景を塗らなくても文字が帯の裏に回り込まない
       （塗ると動画まわりの光＝アンビエントモードの上に黒い枠が浮く） */
    ytd-watch-flexy[mytube-split] #secondary {
      position: sticky; top: 0; align-self: flex-start;
      max-height: calc(100vh - 24px);
      display: flex; flex-direction: column;
      padding-top: 0 !important;
    }
    ytd-watch-flexy[mytube-split] #secondary-inner {
      flex: 1 1 auto; min-height: 0;
      overflow-y: auto; overscroll-behavior: contain;
      scrollbar-width: thin; scrollbar-color: #555 transparent;
    }
    #mytube-side-tabs {
      flex: none;
      display: flex; gap: 8px; margin: 0 0 12px; padding: 12px 0 10px;
      border-bottom: 1px solid rgba(255,255,255,.1);
    }
    #mytube-side-tabs button {
      padding: 6px 12px; border: 0; border-radius: 8px; cursor: pointer;
      font: 500 14px/20px Roboto, "Yu Gothic UI", sans-serif;
      color: #f1f1f1; background: rgba(255,255,255,.1);
    }
    #mytube-side-tabs button:hover { background: rgba(255,255,255,.2); }
    #mytube-side-tabs button[aria-selected="true"] { color: #0f0f0f; background: #f1f1f1; }
    ytd-watch-flexy:not([mytube-split]) #mytube-side-tabs,
    ytd-watch-flexy:not([mytube-split]) #mytube-comments-panel { display: none; }
    ytd-watch-flexy[mytube-split][data-mytube-tab="comments"] #secondary #related { display: none !important; }
    ytd-watch-flexy[mytube-split][data-mytube-tab="related"] #mytube-comments-panel { display: none; }

    /* ライブ配信のチャットを動画の上に流す（操作バーより下、動画より上） */
    #mytube-chat-overlay {
      position: absolute; inset: 0; z-index: 35;
      overflow: hidden; pointer-events: none; contain: strict;
    }
    #mytube-chat-overlay .c {
      position: absolute; left: 0; top: 0; white-space: nowrap; will-change: transform;
      font-weight: 700; line-height: 1.2; font-family: "Yu Gothic UI", Meiryo, Roboto, sans-serif;
      color: #fff; opacity: .9;
      text-shadow: 0 0 3px #000, 1px 1px 1px #000, -1px -1px 1px #000, 1px -1px 1px #000, -1px 1px 1px #000;
    }
    #mytube-chat-overlay .c img { height: 1.1em; width: auto; vertical-align: -.2em; }
    #mytube-chat-overlay .paid { color: #ffd54f; }
    #mytube-chat-overlay .member { color: #7ee787; }
    /* 全画面でコメントを流しているときは YouTube のチャット欄を隠す（閉じると全画面を解除しても閉じたままになるため）。
       隠している間は下の自前のチャット欄からコメントを拾う */
    html.mytube-chat-on ytd-watch-flexy[fullscreen] :is(ytd-live-chat-frame, #panels-full-bleed-container:has(ytd-live-chat-frame)) {
      display: none !important;
    }
    /* 自前のチャット欄（チャット欄を閉じているとき用）。読み込みを止められないよう、消さずに画面の外に置く */
    #mytube-chat-source {
      position: fixed; left: -10000px; top: 0; width: 400px; height: 600px;
      border: 0; opacity: 0; pointer-events: none;
    }
    .mytube-chat-btn {
      display: inline-flex !important; align-items: center; justify-content: center;
      padding: 0 !important; vertical-align: top;
    }
    .mytube-chat-btn[hidden] { display: none !important; }
    .mytube-chat-btn svg { flex: none; }
    .mytube-chat-btn .slash { display: none; }
    .mytube-chat-btn[aria-pressed="false"] .slash { display: inline; }
    .mytube-chat-btn[aria-pressed="false"] svg { opacity: .7; }
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
    applyHistoryFilter();
    attachChat();
  }

  // ---- 履歴ページ：最初は「動画」の絞り込みを選んでおく（ショートを出さない） ----

  const HISTORY_PATH = '/feed/history';
  const VIDEO_CHIP_LABELS = ['動画', 'Videos'];
  const HISTORY_SECTIONS = 'ytd-browse[page-subtype="history"] ytd-item-section-renderer';
  let historyFilterPending = false; // 履歴ページに来たばかりで、まだ「動画」を押していない
  let historyWaitFor = null;        // 「動画」を押す前の一覧（これが入れ替わったら表示する）
  let historyRevealTimer = 0;
  let historySettleTimer = 0;
  let lastPath = '';

  // 「すべて」の一覧が一瞬見えてから切り替わるとちらつくので、切り替わるまで一覧を隠す
  function hideHistory(on) {
    document.documentElement.classList.toggle('mytube-history-loading', on);
    clearTimeout(historyRevealTimer);
    // うまく切り替えられなくても、一覧が出ないままにならないようにする
    if (on) historyRevealTimer = setTimeout(() => { historyFilterPending = false; hideHistory(false); }, 3000);
  }

  // 履歴ページに入ったときだけ押す（その後に自分で「すべて」などを選んだら、そのままにする）
  function onPathChanged() {
    const path = location.pathname;
    if (path === lastPath) return;
    lastPath = path;
    historyFilterPending = path === HISTORY_PATH;
    historyWaitFor = null;
    hideHistory(historyFilterPending);
    if (historyFilterPending) schedule();
  }

  // 画面が切り替わる前に、行き先が履歴ページなら先に隠しておく
  function onNavigateStart(e) {
    const url = e.detail && e.detail.url;
    if (!url) return;
    try {
      if (new URL(url, location.origin).pathname === HISTORY_PATH && lastPath !== HISTORY_PATH) hideHistory(true);
    } catch { /* 無視 */ }
  }

  function applyHistoryFilter() {
    if (historyWaitFor) {
      // 一覧が「動画」の内容に入れ替わったら表示する
      if (!historyWaitFor.isConnected || document.querySelector(HISTORY_SECTIONS) !== historyWaitFor) {
        historyWaitFor = null;
        hideHistory(false);
        return;
      }
      // 同じ部品を使い回して中身だけ入れ替わる場合もあるので、画面の変化が落ち着いたら表示する
      clearTimeout(historySettleTimer);
      historySettleTimer = setTimeout(() => {
        if (!historyWaitFor) return;
        historyWaitFor = null;
        hideHistory(false);
      }, 800);
      return;
    }
    if (!historyFilterPending) return;
    if (location.pathname !== HISTORY_PATH) { historyFilterPending = false; hideHistory(false); return; }
    const page = document.querySelector('ytd-browse[page-subtype="history"]');
    if (!page) return;
    const chips = page.querySelectorAll('yt-chip-cloud-chip-renderer, chip-view-model, chip-shape');
    for (const chip of chips) {
      if (!VIDEO_CHIP_LABELS.includes(chip.textContent.trim())) continue;
      historyFilterPending = false;
      const selected = chip.hasAttribute('selected') ||
        chip.getAttribute('aria-selected') === 'true' ||
        !!chip.querySelector('[aria-selected="true"]');
      if (selected) { hideHistory(false); return; }
      historyWaitFor = document.querySelector(HISTORY_SECTIONS);
      (chip.querySelector('button, [role="tab"]') || chip).click();
      if (!historyWaitFor) hideHistory(false);
      return;
    }
  }

  // ---- 動画ページ：コメントを右の列のタブに移す ----

  let sideTab = 'comments';
  let commentsHome = null; // コメントの元の場所（1列表示に戻すとき用）

  function applyWatchLayout() {
    const flexy = document.querySelector('ytd-watch-flexy');
    if (!flexy) return;
    const comments = flexy.querySelector('ytd-comments#comments');
    const secondary = flexy.querySelector('#secondary');
    const inner = secondary && secondary.querySelector('#secondary-inner');
    const related = inner && inner.querySelector('#related');
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
          inner.scrollTop = 0;
          applyWatchLayout();
        });
        tabs.appendChild(b);
      }
      // タブはスクロールする部分（#secondary-inner）の外、右の列の一番上に置く
      secondary.prepend(tabs);
      // YouTube はウィンドウのスクロールで続きを読み込むので、右の列のスクロールも伝える
      inner.addEventListener('scroll', () => window.dispatchEvent(new Event('scroll')), { passive: true });
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

  // ---- ライブ配信のチャットを動画の上に流す ----
  // 右側のチャット欄（同じサイトの iframe）に発言が増えるのを見張って、プレイヤーの上に流す。
  // アーカイブのチャットも YouTube 側が再生位置に合わせて出してくれるので、そのまま流せばよい。

  const CHAT_DURATION = 5000; // 右端から左端まで流れる時間（ミリ秒）
  const CHAT_LANES = 14;      // 縦に並ぶ行の数
  const CHAT_MAX = 80;        // 同時に流す上限
  const CHAT_BURST = 15;      // 一度にこれより多く増えたら（読み込み直後・シーク直後）流さない
  const CHAT_TYPES = {
    'YT-LIVE-CHAT-TEXT-MESSAGE-RENDERER': '',
    'YT-LIVE-CHAT-PAID-MESSAGE-RENDERER': 'paid',
    'YT-LIVE-CHAT-PAID-STICKER-RENDERER': 'paid',
    'YT-LIVE-CHAT-MEMBERSHIP-ITEM-RENDERER': 'member',
  };

  let chatOn = true;
  let chatItems = null;    // 見張っているチャット欄の発言リスト
  let chatObserver = null;
  let chatIgnoreUntil = 0;
  let chatVideo = null;
  const chatLanes = new Array(CHAT_LANES).fill(null); // 行ごとに最後に流したコメント
  const chatAnims = new Set();

  // チャット欄を閉じても流せるように、そのときは画面の外に自前のチャット欄を読み込んでそこから拾う
  let chatSrc = null;   // { id, src } 動画ごとに、YouTube のチャット欄が開いていたページ
  let ownFrame = null;  // 自前のチャット欄

  function currentVideoId() {
    return location.pathname === '/watch' ? new URLSearchParams(location.search).get('v') : null;
  }

  function isShown(frame) {
    if (frame.closest('ytd-live-chat-frame[collapsed]')) return false;
    const r = frame.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  }

  // 一度もチャット欄を開いていなくても、配信中ならチャットのページは動画IDだけで開ける
  function liveChatSrc(id) {
    try {
      const player = document.querySelector('#movie_player');
      const data = player && player.getVideoData && player.getVideoData();
      if (data && data.isLive) return `${location.origin}/live_chat?v=${encodeURIComponent(id)}`;
    } catch { /* 分からなければなし */ }
    return null;
  }

  function ensureOwnFrame(src) {
    if (ownFrame && ownFrame.isConnected && ownFrame.dataset.src === src) return ownFrame;
    removeOwnFrame();
    if (!document.body) return null;
    ownFrame = document.createElement('iframe');
    ownFrame.id = 'mytube-chat-source';
    ownFrame.dataset.src = src;
    ownFrame.src = src;
    document.body.appendChild(ownFrame);
    return ownFrame;
  }

  function removeOwnFrame() {
    if (ownFrame) ownFrame.remove();
    ownFrame = null;
  }

  function attachChat() {
    const id = currentVideoId();
    const ytFrame = id ? document.querySelector('ytd-live-chat-frame iframe') : null;
    if (ytFrame && /\/live_chat(_replay)?\?/.test(ytFrame.src) && !(chatSrc && chatSrc.id === id && chatSrc.src === ytFrame.src)) {
      chatSrc = { id, src: ytFrame.src };
    }
    const src = id ? (chatSrc && chatSrc.id === id ? chatSrc.src : liveChatSrc(id)) : null;

    let frame = null;
    if (ytFrame && isShown(ytFrame)) {
      frame = ytFrame;
      removeOwnFrame();
    } else if (chatOn && src) {
      frame = ensureOwnFrame(src);
    } else {
      removeOwnFrame();
    }

    let items = null;
    try {
      const doc = frame && frame.contentDocument;
      items = doc && doc.querySelector('yt-live-chat-item-list-renderer #items');
    } catch { /* 読めないときは何もしない */ }

    renderChatButton(!!(ytFrame || src));
    watchVideo();

    if (items === chatItems) return;
    if (chatObserver) chatObserver.disconnect();
    chatObserver = null;
    chatItems = items;
    clearChat();
    if (!items) return;
    chatIgnoreUntil = performance.now() + 1500;
    chatObserver = new MutationObserver(onChatMutations);
    chatObserver.observe(items, { childList: true });
  }

  function onChatMutations(records) {
    if (!chatOn) return;
    const nodes = [];
    for (const r of records) {
      for (const n of r.addedNodes) {
        if (n.nodeType === 1 && n.tagName in CHAT_TYPES) nodes.push(n);
      }
    }
    if (!nodes.length || nodes.length > CHAT_BURST || performance.now() < chatIgnoreUntil) return;
    if (!chatVideo || chatVideo.paused) return;
    // 追加された直後は中身がまだ入っていないことがあるので少し待つ
    setTimeout(() => nodes.forEach(spawnComment), 50);
  }

  // 一時停止・シークに合わせる
  function watchVideo() {
    const video = document.querySelector('#movie_player video');
    if (!video || video === chatVideo) return;
    chatVideo = video;
    video.addEventListener('pause', () => chatAnims.forEach((a) => a.pause()));
    video.addEventListener('play', () => chatAnims.forEach((a) => a.play()));
    video.addEventListener('seeking', () => {
      clearChat();
      chatIgnoreUntil = performance.now() + 1500;
    });
    // アーカイブのチャットは再生位置を教えてもらって進むので、自前のチャット欄には自分で伝える
    // （YouTube のチャット欄が受け取っているのと同じ形のメッセージ）
    video.addEventListener('timeupdate', () => {
      if (!ownFrame || !ownFrame.contentWindow || !ownFrame.dataset.src.includes('/live_chat_replay')) return;
      ownFrame.contentWindow.postMessage({ 'yt-player-video-progress': video.currentTime }, location.origin);
    });
  }

  function clearChat() {
    for (const a of [...chatAnims]) a.cancel();
    chatLanes.fill(null);
  }

  function appendMessage(el, msg) {
    for (const n of msg.childNodes) {
      if (n.nodeType === 3) {
        el.append(n.textContent);
      } else if (n.nodeName === 'IMG') {
        // 絵文字・スタンプは画像のまま出す
        if (/^https:/.test(n.src)) {
          const img = document.createElement('img');
          img.src = n.src;
          img.alt = n.alt || '';
          el.append(img);
        } else if (n.alt) {
          el.append(n.alt);
        }
      } else if (n.nodeType === 1) {
        appendMessage(el, n); // リンクなど
      }
    }
  }

  function buildComment(node) {
    const kind = CHAT_TYPES[node.tagName];
    const el = document.createElement('div');
    el.className = kind ? `c ${kind}` : 'c';
    const amount = node.querySelector('#purchase-amount, #purchase-amount-chip');
    if (amount && amount.textContent.trim()) el.append(amount.textContent.trim() + ' ');
    const msg = node.querySelector('#message');
    if (msg) appendMessage(el, msg);
    return el.textContent.trim() || el.querySelector('img') ? el : null;
  }

  // 前のコメントに重ならない行を探す。見つからなければ -1
  function pickLane(width, W) {
    const speed = (W + width) / CHAT_DURATION;
    for (let i = 0; i < CHAT_LANES; i++) {
      const prev = chatLanes[i];
      if (!prev || !chatAnims.has(prev.anim)) return i;
      const t = Number(prev.anim.currentTime) || 0;
      // 前のコメントの末尾がまだ右端から出きっていない
      if (W - prev.speed * t + prev.width > W - 16) continue;
      // 後のほうが速くても、前の末尾が左端に着くまでに追いつかない
      if (W / speed >= CHAT_DURATION - t) return i;
    }
    return -1;
  }

  function spawnComment(node) {
    if (!chatOn || chatAnims.size >= CHAT_MAX || !node.isConnected) return;
    const player = document.querySelector('#movie_player');
    if (!player) return;
    const el = buildComment(node);
    if (!el) return;

    let overlay = player.querySelector(':scope > #mytube-chat-overlay');
    if (!overlay) {
      overlay = document.createElement('div');
      overlay.id = 'mytube-chat-overlay';
      player.appendChild(overlay);
    }
    const W = overlay.clientWidth;
    const H = overlay.clientHeight;
    if (!W || !H) return;

    const laneH = H / CHAT_LANES;
    el.style.fontSize = `${Math.max(14, laneH / 1.3)}px`;
    el.style.visibility = 'hidden';
    overlay.appendChild(el);
    const width = el.offsetWidth;
    const lane = pickLane(width, W);
    if (lane < 0) { el.remove(); return; }

    el.style.top = `${lane * laneH}px`;
    el.style.visibility = '';
    const anim = el.animate(
      [{ transform: `translateX(${W}px)` }, { transform: `translateX(${-width}px)` }],
      { duration: CHAT_DURATION, easing: 'linear' });
    chatLanes[lane] = { anim, width, speed: (W + width) / CHAT_DURATION };
    chatAnims.add(anim);
    anim.onfinish = anim.oncancel = () => { chatAnims.delete(anim); el.remove(); };
  }

  // 操作バーのオン・オフボタン（チャットがある動画だけ出す）
  const SVG_NS = 'http://www.w3.org/2000/svg';
  function svgPath(d, cls) {
    const p = document.createElementNS(SVG_NS, 'path');
    p.setAttribute('d', d);
    p.setAttribute('fill', 'none');
    p.setAttribute('stroke', '#fff');
    p.setAttribute('stroke-width', '1.8');
    p.setAttribute('stroke-linecap', 'round');
    p.setAttribute('stroke-linejoin', 'round');
    if (cls) p.setAttribute('class', cls);
    return p;
  }

  // 吹き出しの絵は 24 の枠のうち幅 17 を占めるので、歯車の絵と同じ幅になる svg の大きさを返す
  function chatIconSize(ref) {
    try {
      const rect = ref.getBoundingClientRect();
      const box = ref.viewBox && ref.viewBox.baseVal;
      const scale = box && box.width ? rect.width / box.width : 1;
      const drawn = ref.getBBox().width * scale;
      if (drawn > 0) return Math.round(Math.min(40, Math.max(14, drawn * 24 / 17)));
    } catch { /* 測れなければ既定の大きさ */ }
    return 24;
  }

  function renderChatButton(show) {
    const controls = document.querySelector('#movie_player .ytp-right-controls');
    if (!controls) return;
    let btn = controls.querySelector('.mytube-chat-btn');
    if (!btn) {
      btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'ytp-button mytube-chat-btn';
      const svg = document.createElementNS(SVG_NS, 'svg');
      svg.setAttribute('viewBox', '0 0 24 24');
      svg.append(
        svgPath('M5 5h14a1.5 1.5 0 0 1 1.5 1.5v8A1.5 1.5 0 0 1 19 16h-7l-4 3.5V16H5a1.5 1.5 0 0 1-1.5-1.5v-8A1.5 1.5 0 0 1 5 5z'),
        svgPath('M8 9h8M8 12.5h5'),
        svgPath('M4 20L20 4', 'slash'));
      btn.append(svg);
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        setChatOn(!chatOn);
        post({ type: 'chatComments', value: chatOn });
      });
    }
    // 設定（歯車）ボタンのすぐ左に置く
    const settings = controls.querySelector('.ytp-settings-button');
    if (settings) {
      if (settings.previousElementSibling !== btn) settings.before(btn);
    } else if (!btn.isConnected) {
      controls.prepend(btn);
    }
    // アイコンの大きさは歯車の絵の大きさに合わせる（全画面などで変わる）。
    // 歯車の svg は余白込みなので、svg の幅ではなく中の絵の幅を測る
    const size = `${chatIconSize(settings && settings.querySelector('svg'))}px`;
    const svg = btn.querySelector('svg');
    if (svg.style.width !== size) {
      svg.style.width = size;
      svg.style.height = size;
    }
    if (btn.hidden !== !show) btn.hidden = !show;
    const pressed = String(chatOn);
    if (btn.getAttribute('aria-pressed') !== pressed) {
      btn.setAttribute('aria-pressed', pressed);
      btn.title = chatOn ? 'コメントを非表示' : 'コメントを表示';
    }
  }

  function setChatOn(on) {
    chatOn = !!on;
    if (!chatOn) clearChat();
    if (document.documentElement) document.documentElement.classList.toggle('mytube-chat-on', chatOn);
    attachChat();
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
          setChatOn(m.chat !== false);
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
  // YouTube の上部バーなどが一瞬でも見えないよう、CSS はページの中身より先に入れる。
  // このスクリプトは <html> ができる前に動くことがあるので、そのときは <html> ができた瞬間に入れる
  function insertStyle() {
    if (style.isConnected) return;
    (document.head || document.documentElement).appendChild(style);
  }
  if (document.documentElement) {
    insertStyle();
  } else {
    new MutationObserver((_, obs) => {
      if (!document.documentElement) return;
      insertStyle();
      obs.disconnect();
    }).observe(document, { childList: true });
  }

  function setup() {
    insertStyle();
    setDim(dim);
    new MutationObserver((records) => {
      // 流しているコメントの出し入れでは見直さない
      if (records.every((r) => r.target.id === 'mytube-chat-overlay')) return;
      schedule();
    }).observe(document.documentElement, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ['href'],
    });
    onPathChanged();
    // YouTube はページを読み直さずに画面を切り替えるので、切り替わるたびに確かめる
    window.addEventListener('yt-navigate-start', onNavigateStart);
    window.addEventListener('yt-navigate-finish', onPathChanged);
    schedule();
    // チャット欄の中身（iframe）の入れ替わりはこのページの変化として見えないので、定期的に確かめる
    setInterval(attachChat, 1000);
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
