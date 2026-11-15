/* ThixInstall.Cloud as an app (PWA): the service worker, the install button and the clean-up when somebody signs out.
   - The service worker (sw.js) keeps the shell of the app and the last lists that were loaded, for offline use.
   - "App installieren": the browser offers it by an event; the button (data-pwa-install, in the user menu) is shown only when the browser can do it.
   - Signing out clears what the service worker kept on this device (the copies of pages belong to the person who was signed in). */
(function () {
  'use strict';

  if ('serviceWorker' in navigator && window.isSecureContext) {
    window.addEventListener('load', function () { navigator.serviceWorker.register('/sw.js').catch(function () { /* the app works without it */ }); });
  }

  var deferred = null;
  function installButtons(show) {
    Array.prototype.forEach.call(document.querySelectorAll('[data-pwa-install]'), function (b) { b.hidden = !show; });
  }

  window.addEventListener('beforeinstallprompt', function (e) {
    e.preventDefault();
    deferred = e;
    installButtons(true);
  });
  window.addEventListener('appinstalled', function () { deferred = null; installButtons(false); });
  document.addEventListener('click', function (e) {
    var button = e.target.closest && e.target.closest('[data-pwa-install]');
    if (!button || !deferred) { return; }
    deferred.prompt();
    deferred.userChoice.finally(function () { deferred = null; installButtons(false); });
  });

  document.addEventListener('submit', function (e) {
    var form = e.target;
    if (!form || !form.matches || !form.matches('[data-logout]')) { return; }
    if (window.caches) { caches.delete('ti-pages'); caches.delete('ti-images'); }
    if (navigator.serviceWorker && navigator.serviceWorker.controller) { navigator.serviceWorker.controller.postMessage('ti-clear'); }
  });
})();
