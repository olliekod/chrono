// Recording: what Chrono is recording right now (there is no overlay in games; this is where you look), how it
// decides what to record, and the hotkeys that save clips.
(function (root) {
  const Chrono = root.Chrono;
  const { h, icon, bridge } = Chrono;

  const MODES = [
    ['Auto', 'Automatic', 'Chrono finds the game you start and records it. Browsers, Discord, video players and other apps are never recorded.'],
    ['Application', 'Pick a game', "Record one game you choose, even if Chrono doesn't recognise it as a game."],
    ['Display', 'Whole screen', 'Record the screen your active window is on, for as long as Chrono is on.'],
  ];

  let host, appTimer, appSelect, appListOpen = false;

  const keyLabel = (key) => (key === 'Control' ? 'Ctrl' : key);
  const keyPills = (keys) => h('span', { class: 'keys' }, keys.map((k) => h('span', { class: 'key', text: keyLabel(k) })));

  function heroCopy(s) {
    if (s.state === 'recording') {
      return `Chrono keeps the last ${Chrono.plainDuration(s.bufferSeconds)} of ${s.mode === 'Display' ? 'your screen' : s.target}. Press a hotkey to save it as a clip.`;
    }
    if (s.state === 'paused') return 'Recording is paused. Nothing is being kept until you resume.';
    if (s.mode === 'Auto') return 'Chrono is watching for a game to start. Nothing is recorded until one does.';
    if (s.mode === 'Application') return s.selectedApplication ? `Chrono starts recording as soon as ${s.selectedApplication} starts.` : 'Choose a game below and Chrono will record it.';
    return 'Chrono will record your screen.';
  }

  function fact(iconName, label, value) {
    return h('div', { class: 'fact' }, h('div', { class: 'k' }, icon(iconName), label), h('div', { class: 'v', text: value }));
  }

  async function refreshApps() {
    if (!appSelect || appListOpen) return;
    try {
      const { apps } = await bridge.request('getRunningApps');
      const selected = (Chrono.state.status && Chrono.state.status.selectedApplication) || '';
      const names = [...apps];
      if (selected && !names.includes(selected)) names.unshift(selected);

      appSelect.textContent = '';
      appSelect.append(h('option', { value: '', text: 'Choose a game…' }));
      for (const name of names) {
        appSelect.append(h('option', { value: name, text: apps.includes(name) ? name : `${name} (not running)` }));
      }
      appSelect.value = selected;
    } catch { /* the list is a convenience */ }
  }

  function render() {
    const s = Chrono.state.status;
    if (!host || !s) return;
    host.textContent = '';

    const toggle = h('button', {
      class: `btn ${s.enabled ? 'outline' : 'primary'}`,
      onClick: async () => {
        try { await bridge.request('setRecorderEnabled', { enabled: !s.enabled }); } catch (err) { Chrono.toast('error', "Couldn't change recording", err.message); }
      },
    }, s.enabled ? 'Pause recording' : 'Resume recording');

    host.append(
      h('div', { class: `hero ${s.state}` },
        h('span', { class: 'rec-dot' }),
        h('div', {}, h('h2', { text: s.headline }), h('p', { text: heroCopy(s) })),
        h('span', { class: 'spacer' }), toggle),

      h('div', { class: 'facts' },
        fact('monitor', 'Screen', s.screen ? `${s.screen} at ${s.fps} FPS` : `${s.fps} FPS`),
        fact('film', 'Saved clips', s.clipQuality),
        fact('volume', 'Sound', s.audio),
        fact('clock', 'Kept in memory', s.state === 'recording' ? `Last ${Chrono.plainDuration(s.bufferSeconds)}` : 'Nothing right now')),

      h('h3', { class: 'h2', text: 'What Chrono records' }),
      h('p', { class: 'h2-note', text: MODES.find((m) => m[0] === s.mode)[2] }),
      h('div', { class: 'segmented', role: 'group', 'aria-label': 'What to record' },
        MODES.map(([mode, label]) => h('button', {
          'aria-pressed': String(s.mode === mode),
          onClick: async () => {
            try { await bridge.request('setMode', { mode }); } catch (err) { Chrono.toast('error', "Couldn't change the mode", err.message); }
          },
        }, label))));

    if (s.mode === 'Application') {
      appSelect = h('select', {
        class: 'input', 'aria-label': 'Game to record',
        onFocus: () => { appListOpen = true; }, onBlur: () => { appListOpen = false; },
        onChange: async (e) => {
          appListOpen = false;
          try { await bridge.request('setMode', { mode: 'Application', app: e.target.value }); } catch (err) { Chrono.toast('error', "Couldn't choose that game", err.message); }
        },
      });
      host.append(h('div', { class: 'field' }, appSelect));
      refreshApps();
    } else {
      appSelect = null;
    }

    host.append(
      h('h3', { class: 'h2', text: 'Hotkeys' }),
      h('p', { class: 'h2-note', text: 'Press one while you play to save the most recent footage as a clip. It goes straight to your library; nothing is uploaded until you choose to.' }),
      h('div', { class: 'rows' }, (s.hotkeys.length ? s.hotkeys : [null]).map((k) => k
        ? h('div', { class: 'row' }, keyPills(k.keys), h('div', { class: 'grow' }, h('div', { class: 'name', text: k.name }),
            h('div', { class: 'desc', text: `Keeps the last ${Chrono.plainDuration(k.seconds)}` })))
        : h('div', { class: 'row' }, h('div', { class: 'grow' }, h('div', { class: 'desc', text: 'No hotkeys yet.' }),
            h('button', { class: 'btn small', onClick: () => Chrono.nav.go('settings', 'hotkeys') }, 'Add one in Settings'))))),
      h('button', { class: 'btn ghost', onClick: () => Chrono.nav.go('settings', 'hotkeys') }, icon('keyboard'), 'Change hotkeys'));
  }

  Chrono.register('recording', {
    mount(container) {
      host = h('div', { class: 'page' });
      container.append(
        h('div', { class: 'topbar' }, h('span', { class: 'hash', text: '#' }), h('h1', { text: 'Recording' })),
        h('div', { class: 'scroll' }, host));
      render();
      appTimer = setInterval(refreshApps, 3000);
    },
    unmount() { clearInterval(appTimer); host = null; appSelect = null; },
    onStatus() {
      // Rebuilding while the game list is open would close it; the list is refreshed on its own timer.
      if (!appListOpen) render();
    },
  });
})(window);
