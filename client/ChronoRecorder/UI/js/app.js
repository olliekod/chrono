// The shell: sidebar, page switching, the always-visible "what is being recorded" panel, and toasts.
(function (root) {
  const Chrono = root.Chrono;
  const { h, icon, bridge } = Chrono;

  const pages = {};
  const state = { status: null, current: null, counts: {} };
  let content, navButtons = {}, statusParts = {}, brandMark;

  Chrono.register = (name, page) => { pages[name] = page; };
  Chrono.state = state;

  // ------------------------------------------------------------------ toasts

  let toastHost;

  /** A short message in the corner. kind: 'good' | 'error' | 'warn' | undefined. action: { label, onClick }. */
  Chrono.toast = function toast(kind, title, text, action) {
    if (!toastHost) return;
    const item = h('div', { class: `toast ${kind || ''}`, role: kind === 'error' ? 'alert' : 'status' },
      h('div', { class: 'msg' }, h('b', { text: title }), text ? h('span', { text }) : null),
      action ? h('button', { class: 'btn small', onClick: () => { action.onClick(); item.remove(); } }, action.label) : null,
      h('button', { class: 'icon-btn', 'aria-label': 'Dismiss', onClick: () => item.remove() }, icon('x')));
    toastHost.append(item);
    const life = kind === 'error' ? 9000 : 4500;
    setTimeout(() => item.remove(), life);
  };

  // -------------------------------------------------------------------- shell

  function build() {
    const app = root.document.getElementById('app');
    app.textContent = '';

    brandMark = h('span', { class: 'brand-mark' }, h('i'));
    const navItems = [
      ['library', 'Library', 'library'],
      ['recording', 'Recording', 'record'],
      ['settings', 'Settings', 'sliders'],
    ];
    navButtons = {};
    const nav = h('nav', { class: 'nav', 'aria-label': 'Main' });
    for (const [key, label, iconName] of navItems) {
      const count = h('span', { class: 'count' });
      const button = h('button', { class: 'nav-item', onClick: () => go(key) }, icon(iconName), h('span', { text: label }), count);
      navButtons[key] = { button, count };
      nav.append(button);
    }

    statusParts = {
      avatar: h('span', { class: 'avatar' }, h('span', { class: 'initial' }), h('span', { class: 'dot' })),
      name: h('b'),
      line: h('span'),
    };
    const statusPanel = h('div', { class: 'status-panel' },
      h('button', { class: 'status-btn', 'aria-label': 'What Chrono is recording', onClick: () => go('recording') },
        statusParts.avatar, h('span', { class: 'status-text' }, statusParts.name, statusParts.line)),
      h('button', { class: 'icon-btn', 'aria-label': 'Settings', title: 'Settings', onClick: () => go('settings') }, icon('sliders')));

    content = h('section', { class: 'content', id: 'content' });
    toastHost = h('div', { class: 'toasts', 'aria-live': 'polite' });

    app.append(
      h('aside', { class: 'sidebar' }, h('div', { class: 'brand' }, brandMark, h('span', { text: 'Chrono' })), nav, statusPanel),
      content, toastHost);
  }

  function go(name, arg) {
    if (!pages[name]) name = 'library';
    if (state.current && pages[state.current].unmount) pages[state.current].unmount();
    state.current = name;
    for (const [key, { button }] of Object.entries(navButtons)) {
      if (key === name) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
    }
    content.textContent = '';
    pages[name].mount(content, arg);
    try { if (root.location.hash !== '#' + name) history.replaceState(null, '', '#' + name); } catch { /* some pages (file:) refuse; the hash is only a convenience */ }
  }

  Chrono.nav = {
    go,
    setCount(page, n) {
      state.counts[page] = n;
      const entry = navButtons[page];
      if (entry) entry.count.textContent = n > 0 ? String(n) : '';
    },
  };

  function renderStatus() {
    const s = state.status;
    if (!s) return;
    statusParts.avatar.querySelector('.initial').textContent = (s.username || 'C').trim().charAt(0).toUpperCase() || 'C';
    statusParts.avatar.querySelector('.dot').className = `dot ${s.state}`;
    statusParts.name.textContent = s.username || 'Chrono';
    statusParts.line.textContent = s.headline;
    brandMark.className = `brand-mark ${s.state === 'recording' ? 'recording' : ''}`;
  }

  async function boot() {
    build();
    bridge.on('status', (s) => {
      state.status = s;
      renderStatus();
      const page = pages[state.current];
      if (page && page.onStatus) page.onStatus(s);
    });
    bridge.on('toast', (t) => Chrono.toast(t.kind, t.title, t.text));

    try { state.status = await bridge.request('getStatus'); renderStatus(); } catch (err) { console.error(err); }

    const wanted = (root.location.hash || '').replace('#', '');
    // ?section=hotkeys opens that part of Settings (used when designing the pages)
    go(pages[wanted] ? wanted : 'library', new URLSearchParams(root.location.search).get('section') || undefined);
  }

  root.document.addEventListener('DOMContentLoaded', boot);
})(window);
