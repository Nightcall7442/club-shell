/*
 * Service worker of the cash desk (PWA): makes the console installable as an app and opens it quickly. It keeps only the
 * console's own files; the API (the server's origin, or /api/ on this one) is never touched, so balances, sessions and
 * shifts always come live from the server.
 *   - pages: network first (a new deploy shows at once), the last copy when the network is down;
 *   - /assets/*: hashed by Vite, so cache first — a file under a name never changes;
 *   - icons, manifest, fonts: network first with the cached copy as a fallback.
 */
const CACHE = 'clubshell-admin-v1';
const MAX_ENTRIES = 150;

self.addEventListener('install', () => {
  void self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    (async () => {
      for (const key of await caches.keys()) {
        if (key !== CACHE) await caches.delete(key);
      }
      await self.clients.claim();
    })(),
  );
});

/** Old builds leave hashed files behind; keep the cache bounded (oldest entries go first). */
async function trim(cache) {
  const keys = await cache.keys();
  for (let i = 0; i < keys.length - MAX_ENTRIES; i++) await cache.delete(keys[i]);
}

async function networkFirst(request) {
  const cache = await caches.open(CACHE);
  try {
    const response = await fetch(request);
    if (response.ok && response.type === 'basic') {
      await cache.put(request, response.clone());
      void trim(cache);
    }
    return response;
  } catch (error) {
    const cached = await cache.match(request, { ignoreSearch: request.mode === 'navigate' });
    if (cached) return cached;
    throw error;
  }
}

async function cacheFirst(request) {
  const cache = await caches.open(CACHE);
  const cached = await cache.match(request);
  if (cached) return cached;
  const response = await fetch(request);
  if (response.ok && response.type === 'basic') {
    await cache.put(request, response.clone());
    void trim(cache);
  }
  return response;
}

self.addEventListener('fetch', (event) => {
  const { request } = event;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  // The API (another origin, or /api/ behind one reverse proxy) and anything else not ours goes straight to the network.
  if (url.origin !== self.location.origin || url.pathname.startsWith('/api/') || request.headers.has('Authorization')) return;
  event.respondWith(url.pathname.startsWith('/assets/') ? cacheFirst(request) : networkFirst(request));
});
