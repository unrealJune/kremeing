// Service worker for kremeing web push.
// Lifecycle:
//   install   → skipWaiting so updates activate immediately on next load
//   activate  → clients.claim so already-open tabs get the new SW
//   push      → showNotification with our payload shape
//   notificationclick → focus an existing tab (or open one) at the
//                       payload's URL (relative to our origin)

const SW_VERSION = '0.5.0';

self.addEventListener('install', (event) => {
  event.waitUntil(self.skipWaiting());
});

self.addEventListener('activate', (event) => {
  event.waitUntil(self.clients.claim());
});

self.addEventListener('push', (event) => {
  // event.data can be null for VAPID-only test pushes; default sensibly.
  let payload = {};
  if (event.data) {
    try { payload = event.data.json(); }
    catch { payload = { title: event.data.text() }; }
  }
  const title = payload.title || 'Hot Light';
  const options = {
    body: payload.body || 'Hot doughnuts ready now',
    // Tag collapses repeats per store: a second notification for the
    // same store while the first is undismissed replaces it instead of
    // stacking.
    tag: payload.storeId ? `kremeing-${payload.storeId}` : 'kremeing',
    data: { url: payload.url || '/' },
    renotify: false,
  };
  event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const targetUrl = event.notification.data?.url || '/';

  event.waitUntil((async () => {
    const allClients = await self.clients.matchAll({
      type: 'window',
      includeUncontrolled: true,
    });
    for (const client of allClients) {
      if (client.url.startsWith(self.location.origin)) {
        try { await client.navigate(targetUrl); } catch {}
        return client.focus();
      }
    }
    return self.clients.openWindow(targetUrl);
  })());
});
