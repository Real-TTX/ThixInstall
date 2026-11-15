/* ThixInstall.Cloud service worker: the app as a PWA - small on purpose.
   - The shell (toolkit, styles, scripts, icons, the offline page): from the cache first, refreshed in the background.
   - The start page and the lists of devices, actions and bundles come from the network, and a copy is kept: without a network the last copy is
     shown (marked as a copy, offline.js shows "Offline"), any other page shows the offline page with a button to try again (it also tries by itself).
   - The icons of the apps are kept (they rarely change).
   - The API (/api, except the icons), downloads of bundles, every change (POST) and the sign-in are never touched.
   - Signing out clears the copies (pwa.js sends "ti-clear"). */
const VERSION = 'thixinstall-v2';
const PAGES = 'ti-pages';
const IMAGES = 'ti-images';
const SHELL = [
  '/offline.html', '/js/offline.js', '/js/app.js', '/js/pwa.js', '/css/app.css',
  '/matui/matui.css', '/matui/matui.js', '/matui/matui.boot.js',
  '/img/favicon.svg', '/img/logo.svg', '/img/icon-192.png', '/img/icon-512.png', '/manifest.webmanifest',
];
const MAX_PAGES = 30;
const MAX_IMAGES = 300;

self.addEventListener('install', event => {
  event.waitUntil(caches.open(VERSION).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

// an older version of the shell goes (the copies of pages and icons stay: they are the person's until the sign-out)
self.addEventListener('activate', event => {
  event.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(k => k !== VERSION && k !== PAGES && k !== IMAGES).map(k => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener('message', event => {
  if (event.data === 'ti-clear') {
    event.waitUntil(Promise.all([caches.delete(PAGES), caches.delete(IMAGES)]));
  }
});

function isStatic(url) {
  return /^\/(matui|css|js|img)\//.test(url.pathname) || url.pathname === '/manifest.webmanifest' || url.pathname === '/offline.html';
}

function isKeptPage(url) {
  return url.pathname === '/' || /^\/(Devices|Devices\/Detail|Actions|Bundles)$/i.test(url.pathname);
}

function trim(cache, max) {
  return cache.keys().then(keys => (keys.length > max ? cache.delete(keys[0]).then(() => trim(cache, max)) : undefined));
}

/** A page that comes from the copy says so (<html data-offline-copy>), so the page can show it. */
function markCopy(response) {
  return response.text().then(html => new Response(html.replace('<html ', '<html data-offline-copy="true" '), {
    status: 200,
    headers: { 'Content-Type': 'text/html; charset=utf-8' },
  }));
}

function offlinePage(url) {
  return caches.match('/offline.html', { ignoreSearch: true }).then(hit => {
    if (!hit) { return Response.error(); }
    return hit.text().then(html => new Response(html.replace('data-from=""', 'data-from="' + url.pathname.replace(/"/g, '') + '"'), {
      status: 503,
      headers: { 'Content-Type': 'text/html; charset=utf-8' },
    }));
  });
}

/** A page from the network (a copy is kept), without one the last copy, without that the offline page. */
function page(request, url) {
  return fetch(request).then(response => {
    // only a real page is kept (not the sign-in a redirect led to)
    if (response.ok && !response.redirected && isKeptPage(url)) {
      const copy = response.clone();
      caches.open(PAGES).then(cache => cache.put(request, copy).then(() => trim(cache, MAX_PAGES)));
    }
    return response;
  }).catch(() => caches.open(PAGES)
    .then(cache => cache.match(request))
    .then(hit => (hit ? markCopy(hit) : offlinePage(url))));
}

/** From the cache first, refreshed in the background (the shell). A versioned address (?v=) also finds the copy of the plain one. */
function shell(request) {
  return caches.open(VERSION).then(cache => cache.match(request).then(hit => {
    const fresh = fetch(request).then(response => {
      if (response.ok) { cache.put(request, response.clone()); }
      return response;
    }).catch(() => hit || cache.match(request, { ignoreSearch: true }));
    return hit || fresh;
  }));
}

/** The icon of an app: from the network when it can be had (and kept), else the copy. */
function image(request) {
  return caches.open(IMAGES).then(cache => fetch(request).then(response => {
    if (response.ok) { cache.put(request, response.clone()).then(() => trim(cache, MAX_IMAGES)); }
    return response;
  }).catch(() => cache.match(request).then(hit => hit || Response.error())));
}

self.addEventListener('fetch', event => {
  const request = event.request;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin || url.pathname === '/sw.js' || url.pathname === '/health') return;

  if (/^\/api\/v1\/packages\/\d+\/icon$/.test(url.pathname)) {
    event.respondWith(image(request));
    return;
  }

  if (url.pathname.startsWith('/api/') || url.pathname.endsWith('/download')) return;

  if (request.mode === 'navigate') {
    if (url.pathname.startsWith('/Account/Login') || url.pathname.startsWith('/Account/Setup')) {
      event.respondWith(fetch(request).catch(() => offlinePage(url)));
      return;
    }
    event.respondWith(page(request, url));
    return;
  }

  if (isStatic(url)) {
    event.respondWith(shell(request));
  }
});
