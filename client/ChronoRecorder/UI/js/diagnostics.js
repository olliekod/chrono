// Diagnostics: how Chrono is performing on this PC. Live numbers while a game is recorded (processor, memory, the
// graphics card's video encoder, encoding speed, dropped frames), the hardware it is running on, anything worth
// attention, and a report to copy and send to whoever is looking into a problem. Nothing is sent anywhere by Chrono.
(function (root) {
  const Chrono = root.Chrono;
  const { h, icon, bridge } = Chrono;

  const REFRESH_MS = 2000;
  const TONE_ICON = { good: 'check', info: 'clock', warn: 'alert', bad: 'alert' };

  let host = null, timer = null, busy = false, data = null, failure = '';

  function findingsCard(list) {
    return h('section', { class: 'diag-findings', 'aria-label': 'What needs attention' },
      list.map((f) => h('div', { class: `diag-finding ${f.tone}` }, icon(TONE_ICON[f.tone] || 'clock'), h('span', { text: f.text }))));
  }

  function sectionCard(section) {
    const rows = [];
    for (const row of section.rows) {
      rows.push(h('dt', { text: row.label }), h('dd', { class: row.tone || '', text: row.value }));
    }
    return h('section', { class: 'diag-card' }, h('h3', { text: section.title }), h('dl', { class: 'diag-list' }, rows));
  }

  function eventsCard(events) {
    if (!events.length) return null;
    return h('section', { class: 'diag-card wide' }, h('h3', { text: 'Recent events' }),
      h('pre', { class: 'diag-events', text: events.join('\n') }));
  }

  function render() {
    if (!host) return;
    host.textContent = '';

    host.append(h('p', { class: 'diag-note',
      text: 'This shows how Chrono is running on this PC. Copy the report and paste it to whoever asked for it. It has no name, folders, server address or key in it, and Chrono never sends it anywhere by itself.' }));

    if (failure) host.append(h('p', { class: 'diag-note', text: failure }));
    if (!data) return;

    host.append(
      findingsCard(data.findings),
      h('div', { class: 'diag-grid' }, data.sections.map(sectionCard)),
      eventsCard(data.events));
  }

  async function refresh() {
    if (busy || !host) return;   // one request at a time; the numbers take a moment to gather
    busy = true;
    try {
      data = await bridge.request('getDiagnostics');
      failure = '';
    } catch (err) {
      failure = `Couldn't read the diagnostics: ${err.message}`;
    } finally {
      busy = false;
    }
    render();
  }

  async function copyReport() {
    try {
      await bridge.request('copyDiagnostics');
      Chrono.toast('good', 'Report copied', 'Paste it into your chat.');
    } catch (err) {
      Chrono.toast('error', "Couldn't copy the report", err.message);
    }
  }

  async function openLogs() {
    try { await bridge.request('openLogsFolder'); } catch (err) { Chrono.toast('error', "Couldn't open the folder", err.message); }
  }

  Chrono.register('diagnostics', {
    mount(container) {
      host = h('div', { class: 'page' });
      container.append(
        h('div', { class: 'topbar' }, h('span', { class: 'hash', text: '#' }), h('h1', { text: 'Diagnostics' }),
          h('span', { class: 'spacer' }),
          h('button', { class: 'btn ghost', onClick: openLogs }, icon('folder'), 'Open log folder'),
          h('button', { class: 'btn primary', onClick: copyReport }, icon('copy'), 'Copy report')),
        h('div', { class: 'scroll' }, host));
      data = null; failure = '';
      render();
      refresh();
      timer = setInterval(refresh, REFRESH_MS);
    },
    unmount() { clearInterval(timer); timer = null; host = null; },
  });
})(window);
