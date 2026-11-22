// MyTube: YouTube に限らず、アプリで開くどのサイトにも差し込むスクリプト。
// サイトの見た目には手を入れず、アプリとしての操作（画面の選択・マウスジェスチャー・視聴履歴の記録）だけを受け持つ。
(() => {
  'use strict';
  if (window.top !== window) return;
  if (window.__mytubeCommon) return;
  window.__mytubeCommon = true;

  const host = window.chrome && window.chrome.webview;
  const post = (msg) => { if (host) host.postMessage(msg); };

  // 分割画面で、クリックした（フォーカスが来た）画面をアプリ側で「選択中」にする
  window.addEventListener('pointerdown', () => post({ type: 'activate' }), true);
  window.addEventListener('focus', () => post({ type: 'activate' }));

  // マウスジェスチャー：右ボタンを押したまま左へ動かすと「戻る」、右へ動かすと「進む」。
  // ジェスチャーとして扱ったときは、離したあとに出る右クリックメニューを止める。
  const GESTURE_MIN = 60; // これより短い動きはただの右クリックとみなす
  let gesture = null;
  let suppressMenu = false;
  window.addEventListener('mousedown', (e) => {
    gesture = e.button === 2 ? { x: e.clientX, y: e.clientY } : null;
    suppressMenu = false;
  }, true);
  window.addEventListener('mouseup', (e) => {
    if (e.button !== 2 || !gesture) return;
    const dx = e.clientX - gesture.x;
    const dy = e.clientY - gesture.y;
    gesture = null;
    // 横方向にはっきり動いたときだけ
    if (Math.abs(dx) < GESTURE_MIN || Math.abs(dx) < Math.abs(dy) * 2) return;
    suppressMenu = true;
    post({ type: 'gesture', action: dx < 0 ? 'back' : 'forward' });
  }, true);
  window.addEventListener('contextmenu', (e) => {
    if (!suppressMenu) return;
    suppressMenu = false;
    e.preventDefault();
    e.stopImmediatePropagation();
  }, true);

  // 視聴履歴：このページで動画（音声）を合わせて 30 秒再生したら、アプリに知らせる。
  // 動画のページかどうかはアプリ側で URL から見分ける。その後も 5 分ごとに知らせて、見た日時とタイトルを新しくする
  const WATCH_FIRST = 30;
  const WATCH_AGAIN = 300;
  const newWatch = () => ({ url: location.href, played: 0, next: WATCH_FIRST, last: 0 });
  let watch = newWatch();

  const pageTitle = () => {
    // Netflix の再生画面はページのタイトルが「Netflix」だけなので、プレーヤーに出ている作品名を使う
    const netflix = document.querySelector('[data-uia="video-title"]');
    if (netflix && netflix.textContent.trim()) {
      return [...netflix.children].map((c) => c.textContent.trim()).filter(Boolean).join(' ') ||
        netflix.textContent.trim();
    }
    return document.title;
  };
  const pageImage = () => {
    const meta = document.querySelector('meta[property="og:image"], meta[name="og:image"], meta[name="twitter:image"]');
    return meta ? meta.content : '';
  };

  // timeupdate は泡立たない（親へ伝わらない）が、捕捉（capture）ならページのどの動画のものも受け取れる
  document.addEventListener('timeupdate', (e) => {
    const media = e.target;
    if (!(media instanceof HTMLMediaElement) || media.paused) return;
    // 同じページのまま別の動画へ移ったとき（YouTube など）は数え直す
    if (watch.url !== location.href) watch = newWatch();
    // 動画がいくつ流れていても二重に数えないよう、イベントの間の実際の経過時間を足す（止まっていた間は足さない）
    const now = performance.now();
    if (watch.last && now - watch.last < 2000) watch.played += (now - watch.last) / 1000;
    watch.last = now;
    if (watch.played < watch.next) return;
    watch.next += WATCH_AGAIN;
    post({ type: 'watched', url: location.href, title: pageTitle(), image: pageImage() });
  }, true);
})();
