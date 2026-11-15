/* ThixInstall.Cloud without a network - small on purpose.
   - A page of the app shows "Offline" in the top bar while the device has no network; a click tries again (reloads once the network is back).
   - A page the service worker took from its copy (the last list that was loaded) says so in a note above the content.
   - The offline page (offline.html) tries again by itself as soon as the network is back, every 10 seconds, and by its button. */
(function () {
  'use strict';

  var mark = document.getElementById('ti-offline');
  var copy = document.documentElement.hasAttribute('data-offline-copy');
  function badge() {
    if (!mark) { return; }
    var off = navigator.onLine === false || copy;
    mark.hidden = !off;
    if (off) { mark.title = 'Keine Verbindung – angezeigt wird der zuletzt geladene Stand. Klicken, um es erneut zu versuchen.'; }
  }
  if (mark) {
    mark.addEventListener('click', function () { if (navigator.onLine !== false) { location.reload(); } else { badge(); } });
    window.addEventListener('online', badge);
    window.addEventListener('offline', badge);
    badge();
  }

  // the offline page
  var page = document.getElementById('ti-offline-page');
  if (!page) { return; }
  var retry = document.getElementById('ti-retry');
  var status = document.getElementById('ti-retry-status');
  var target = (function () {
    var from = page.getAttribute('data-from') || '';
    return from.charAt(0) === '/' && from.charAt(1) !== '/' ? from : '/Devices';
  })();

  function check(showStatus) {
    return fetch('/health', { cache: 'no-store' })
      .then(function (r) { if (r.ok) { location.replace(target); } else { throw new Error(); } })
      .catch(function () { if (showStatus) { status.hidden = false; } });
  }

  retry.addEventListener('click', function () { status.hidden = true; check(true); });
  window.addEventListener('online', function () { check(false); });
  setInterval(function () { if (navigator.onLine !== false) { check(false); } }, 10000);
})();
