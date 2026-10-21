// Workbench service worker. It makes the app installable and keeps the app shell's static files (scripts, styles,
// fonts, icons) for a faster start. It never caches pages, API responses or receipts: everything with business or
// financial data comes from the server every time. Pages need a live connection (Blazor Server), so a navigation
// that fails offline gets a static "you're offline" page instead.
const VERSION = "workbench-shell-v2";
const OFFLINE_URL = "offline.html";
const PRECACHE = [OFFLINE_URL, "icons/icon-192.png", "manifest.webmanifest"];

// Only these static paths are ever cached; anything else (pages, /api, /_blazor, /account, receipts) goes to the network.
const STATIC_PREFIXES = ["/_framework/", "/_content/", "/icons/", "/js/"];
const STATIC_EXTENSIONS = /\.(?:js|css|woff2?|ttf|png|svg|ico|webmanifest)$/i;
const NEVER = ["/api/", "/_blazor", "/account/", "/hubs/", "/scim/", "/health/"];

self.addEventListener("install", event => {
  event.waitUntil(caches.open(VERSION).then(cache => cache.addAll(PRECACHE)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", event => {
  event.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k !== VERSION).map(k => caches.delete(k))))
      .then(() => self.clients.claim()));
});

function isCacheable(url) {
  if (url.origin !== self.location.origin || url.search.length > 0) {
    return false;
  }

  const path = url.pathname;
  if (NEVER.some(p => path.startsWith(p))) {
    return false;
  }

  return STATIC_PREFIXES.some(p => path.startsWith(p)) || (path.lastIndexOf("/") === 0 && STATIC_EXTENSIONS.test(path));
}

self.addEventListener("fetch", event => {
  const request = event.request;
  if (request.method !== "GET") {
    return;
  }

  // Pages: the browser loads them itself (never stored). Re-fetching navigations here broke redirect chains such as
  // sign-out (Chrome rejected the worker's response with a network error page), so the worker only answers a
  // navigation when the device is offline, with the static offline page.
  if (request.mode === "navigate") {
    if (self.navigator && self.navigator.onLine === false) {
      event.respondWith(caches.match(OFFLINE_URL).then(page => page || Response.error()));
    }
    return;
  }

  const url = new URL(request.url);
  if (!isCacheable(url)) {
    return;
  }

  // Static files: answer from the cache when we have them and refresh in the background.
  event.respondWith(caches.open(VERSION).then(async cache => {
    const cached = await cache.match(request);
    const network = fetch(request).then(response => {
      const cacheControl = response.headers.get("Cache-Control") || "";
      if (response.ok && response.type === "basic" && !/no-store|private/i.test(cacheControl)) {
        cache.put(request, response.clone());
      }
      return response;
    });
    if (cached) {
      event.waitUntil(network.catch(() => undefined));
      return cached;
    }
    return network;
  }));
});
