// The link to the Chrono app. Requests get a reply (a promise); the app can also push events (library changed,
// status changed, upload progress). Outside the app (a plain browser tab, for design work) dev/mock.js takes over.
(function (root) {
  const Chrono = root.Chrono || (root.Chrono = {});
  const webview = root.chrome && root.chrome.webview;

  const pending = new Map();
  const listeners = new Map();
  let nextId = 1;

  function on(event, handler) {
    if (!listeners.has(event)) listeners.set(event, new Set());
    listeners.get(event).add(handler);
    return () => listeners.get(event).delete(handler);
  }

  function emit(event, data) {
    for (const handler of listeners.get(event) || []) {
      try { handler(data); } catch (err) { console.error(`handler for ${event} failed`, err); }
    }
  }

  function request(action, payload) {
    return new Promise((resolve, reject) => {
      const id = nextId++;
      pending.set(id, { resolve, reject });
      // requestId, not id: payloads carry their own id (a clip's), which must not overwrite this one.
      webview.postMessage({ ...(payload || {}), requestId: id, action });
    });
  }

  if (webview) {
    webview.addEventListener('message', (e) => {
      const message = e.data;
      if (message && typeof message.requestId === 'number') {
        const waiting = pending.get(message.requestId);
        if (!waiting) return;
        pending.delete(message.requestId);
        if (message.ok) waiting.resolve(message.data);
        else waiting.reject(new Error(message.error || 'Something went wrong.'));
      } else if (message && message.event) {
        emit(message.event, message.data);
      }
    });
  }

  Chrono.bridge = { request, on, emit, isMock: !webview };
})(window);
