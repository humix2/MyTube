// MyTube: YouTube 以外のサイトの見た目の調整（サイトごとに CSS を足す）。
// YouTube の調整は作り込みが多いので inject.js にある。
(() => {
  'use strict';
  if (window.top !== window) return;
  if (window.__mytubeSites) return;
  window.__mytubeSites = true;

  const RULES = [
    {
      // Prime Video（amazon.co.jp の /gp/video/ 以下）：上部の Amazon のバー（検索・カート・「すべて」メニューなど）を隠し、
      // その下の Prime Video のメニュー（ホーム・映画・TV番組…）だけを残す
      host: /(^|\.)amazon\.co\.jp$/,
      path: /^\/gp\/video(\/|$)/,
      css: `
        #navbar-main, #nav-subnav, #nav-progressive-subnav, #skiplink { display: none !important; }
      `,
    },
  ];

  const rule = RULES.find((r) => r.host.test(location.hostname) && r.path.test(location.pathname));
  if (!rule) return;

  const style = document.createElement('style');
  style.textContent = rule.css;
  // ページの中身より先に入れて、隠す部分が一瞬でも見えないようにする
  function insertStyle() {
    if (style.isConnected) return;
    // このスクリプトが動く時点では <html> もまだないことがある（そのときは、できてから入れる）
    const parent = document.head || document.documentElement;
    if (parent) parent.appendChild(style);
  }
  insertStyle();
  // <html>・<head> ができたときや、ページ側で消されたときに入れ直す
  new MutationObserver(insertStyle).observe(document, { childList: true, subtree: true });
})();
