// MyTube: YouTube に限らず、アプリで開くどのサイトにも差し込むスクリプト。
// サイトの見た目には手を入れず、アプリとしての操作（画面の選択・マウスジェスチャー）だけを受け持つ。
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
})();
