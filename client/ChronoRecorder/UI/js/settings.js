// Settings. The whole config object is loaded, edited in place and sent back whole, so a setting this page doesn't
// know about is never reset. Changes wait behind a "Save changes" bar, like Discord's user settings.
(function (root) {
  const Chrono = root.Chrono;
  const { h, icon, bridge } = Chrono;

  const SECTIONS = [
    ['recording', 'Recording'],
    ['audio', 'Sound'],
    ['hotkeys', 'Hotkeys'],
    ['upload', 'Uploading'],
    ['app', 'App'],
  ];

  let config = null, saved = '', section = 'recording', recommended = null;
  let body, nav, barHost, offs = [], listening = null, listenHandler = null;

  const dirty = () => config && JSON.stringify(config) !== saved;

  // ------------------------------------------------------------ small controls

  function field(label, control, hint, warn) {
    return h('div', { class: 'field' }, h('label', { text: label }), control, hint ? h('div', { class: `hint ${warn ? 'warn' : ''}`, text: hint }) : null);
  }

  function select(value, options, onChange) {
    const el = h('select', { class: 'input' }, options.map(([v, label]) => h('option', { value: v, text: label })));
    el.value = value;
    el.addEventListener('change', () => { onChange(el.value); touched(); });
    return el;
  }

  function toggle(name, desc, checked, onChange) {
    const input = h('input', { type: 'checkbox', role: 'switch', checked: checked ? true : null, 'aria-label': name });
    input.checked = checked;
    input.addEventListener('change', () => { onChange(input.checked); touched(); });
    return h('label', { class: 'switch-row' }, h('div', { class: 'grow' }, h('div', { class: 'name', text: name }), h('div', { class: 'desc', text: desc })),
      h('span', { class: 'switch' }, input, h('i')));
  }

  function textInput(value, onInput, attrs) {
    const el = h('input', { class: 'input', type: 'text', value: value || '', ...(attrs || {}) });
    el.addEventListener('input', () => { onInput(el.value); touched(false); });
    return el;
  }

  // ------------------------------------------------------------------ sections

  function recordingSection() {
    const bitrate = h('input', {
      class: 'input', style: { maxWidth: '260px' }, type: 'number', min: 1000, max: 80000, step: 500,
      placeholder: recommended ? `${recommended.kbps} (recommended)` : 'Recommended',
      'aria-label': 'Video bitrate in kilobits per second',
    });
    bitrate.value = config.Bitrate > 0 ? config.Bitrate : '';
    bitrate.addEventListener('input', () => { config.Bitrate = parseInt(bitrate.value, 10) > 0 ? parseInt(bitrate.value, 10) : 0; touched(false); });

    const hint = h('div', { class: 'hint' });
    const paintHint = () => {
      hint.textContent = recommended
        ? `Recommended: ${recommended.kbps} kbps for your ${recommended.size} screen at ${recommended.fps} FPS. Leave this empty to use it, or type a number. Higher is sharper and makes bigger files.`
        : 'Leave this empty and Chrono picks a good value for your screen.';
    };
    paintHint();
    bitrate.dataset.hint = '1';
    hintPainter = paintHint;

    return [
      h('h2', { text: 'Recording' }),
      field('Clip size', select(config.Resolution || '1920x1080', [
        ['native', 'Original size (largest files)'], ['2560x1440', '1440p'], ['1920x1080', '1080p (recommended)'], ['1280x720', '720p (smallest files)'],
      ], (v) => { config.Resolution = v; }),
      'Clips are shrunk on your graphics card right after you save them, which takes a few seconds. The game itself is always recorded at full size, so this never slows it down.'),
      field('Frame rate', select(String(config.Fps || 60), [['30', '30 FPS'], ['60', '60 FPS'], ['120', '120 FPS'], ['144', '144 FPS']],
        (v) => { config.Fps = parseInt(v, 10); loadRecommended(); })),
      h('div', { class: 'field' }, h('label', { text: 'Video bitrate (kbps)' }), bitrate, hint),
      field('Encoder', select(config.Encoder || 'auto', [
        ['auto', 'Automatic (best for this PC)'], ['h264_nvenc', 'NVIDIA graphics card'], ['h264_amf', 'AMD graphics card'], ['h264_qsv', 'Intel graphics'], ['libx264', 'Processor (slow)'],
      ], (v) => { config.Encoder = v; }), 'Leave this on Automatic unless clips fail to save. Recording on your processor is much heavier on the game.'),
    ];
  }

  let hintPainter = () => {};

  function audioSection() {
    return [
      h('h2', { text: 'Sound' }),
      toggle('Record game sound', 'Everything you hear: the game, Discord voice chat and music.', config.RecordAudio !== false, (v) => { config.RecordAudio = v; }),
      toggle('Record my microphone', 'Mix your own voice into clips. Off unless you turn it on.', config.RecordMicrophone === true, (v) => { config.RecordMicrophone = v; }),
    ];
  }

  function nextFreeHotkey() {
    const taken = new Set(config.Hotkeys.map((k) => Chrono.hotkeyParts(k).join('+')));
    for (const key of ['End', 'Home', 'Insert', 'Delete', 'PageUp', 'PageDown']) {
      if (!taken.has(['Control', key].join('+'))) return { Name: 'New clip', Key: key, Modifiers: ['Control'], ClipLengthSeconds: 30 };
    }
    return { Name: 'New clip', Key: 'F9', Modifiers: [], ClipLengthSeconds: 30 };
  }

  function hotkeysSection() {
    const rows = h('div');
    const info = h('div', { class: 'hint', style: { marginTop: '16px' } });

    const paint = () => {
      rows.textContent = '';
      const combos = config.Hotkeys.map((k) => Chrono.hotkeyParts(k).join('+'));
      config.Hotkeys.forEach((k, index) => {
        const name = textInput(k.Name, (v) => { k.Name = v; }, { 'aria-label': 'Name', placeholder: 'Name', maxlength: 40 });
        const capture = h('button', { class: 'capture', type: 'button', 'aria-label': `Keys for ${k.Name}. Click, then press the keys.` });
        const showKeys = () => {
          capture.classList.remove('listening');
          capture.textContent = '';
          const parts = Chrono.hotkeyParts(k);
          if (!parts.length) capture.append('Click to set');
          parts.forEach((p, i) => capture.append(i ? ' + ' : '', h('span', { class: 'key', text: p === 'Control' ? 'Ctrl' : p })));
        };
        showKeys();
        capture.addEventListener('click', () => startListening(k, capture, showKeys));

        const length = h('div', { class: 'unit' });
        const secs = h('input', { class: 'input', type: 'number', min: 5, max: 600, value: k.ClipLengthSeconds, 'aria-label': 'Seconds to keep' });
        secs.addEventListener('input', () => { k.ClipLengthSeconds = Math.max(0, parseInt(secs.value, 10) || 0); touched(false); updateInfo(); });
        length.append(secs);

        const remove = h('button', { class: 'icon-btn', type: 'button', 'aria-label': `Remove ${k.Name}`, title: 'Remove',
          onClick: () => { config.Hotkeys.splice(index, 1); paint(); touched(); } }, icon('trash'));

        const duplicate = combos.filter((c) => c === combos[index]).length > 1;
        rows.append(h('div', { class: 'hotkey-row' }, name, capture, length, remove));
        if (duplicate) rows.append(h('div', { class: 'hint warn', style: { margin: '-2px 0 8px' }, text: 'Another hotkey uses the same keys.' }));
      });
      updateInfo();
    };

    const updateInfo = () => {
      const buffer = Chrono.bufferSeconds(config.Hotkeys);
      info.textContent = config.Hotkeys.length
        ? `Chrono keeps the last ${Chrono.plainDuration(buffer)} of footage while you play, enough for your longest hotkey. It is worked out for you.`
        : 'Add a hotkey to start saving clips.';
    };

    paint();
    return [
      h('h2', { text: 'Hotkeys' }),
      h('p', { class: 'h2-note', text: 'Press one while you play to save the most recent footage as a clip. Click a set of keys, then press the new ones.' }),
      rows,
      h('button', { class: 'btn', type: 'button', style: { marginTop: '8px' }, onClick: () => { config.Hotkeys.push(nextFreeHotkey()); paint(); touched(); } }, icon('keyboard'), 'Add a hotkey'),
      info,
    ];
  }

  function startListening(hotkey, button, restore) {
    stopListening();
    button.classList.add('listening');
    button.textContent = 'Press the keys…';
    const restoreView = restore;

    listenHandler = (e) => {
      if (['Control', 'Shift', 'Alt', 'Meta'].includes(e.key)) return;   // wait for the actual key
      e.preventDefault(); e.stopPropagation();
      if (e.key === 'Escape') { stopListening(); restoreView(); return; }

      const key = Chrono.hotkeyName(e.code);
      const mods = Chrono.modifiersOf(e);
      const problem = key ? Chrono.hotkeyProblem(mods, key) : 'Chrono can use letters, numbers, F1 to F24, PageUp, PageDown, Home, End, Insert, Delete, Space and Enter.';
      if (problem) { Chrono.toast('warn', "That key can't be used", problem); return; }

      hotkey.Key = key; hotkey.Modifiers = mods;
      stopListening();
      touched();
      render();
    };
    listening = button;
    root.addEventListener('keydown', listenHandler, true);
  }

  function stopListening() {
    if (listenHandler) root.removeEventListener('keydown', listenHandler, true);
    listenHandler = null; listening = null;
  }

  function uploadSection() {
    const key = textInput(config.UploadKey, (v) => { config.UploadKey = v; }, { type: 'password', autocomplete: 'off', spellcheck: 'false', 'aria-label': 'Upload key' });
    return [
      h('h2', { text: 'Uploading' }),
      h('p', { class: 'h2-note', text: 'Uploading puts a clip on your own server and gives you a link that plays in Discord. Nothing is uploaded unless you press Upload on a clip.' }),
      field('Server address', textInput(config.ApiUrl, (v) => { config.ApiUrl = v; }, { placeholder: 'https://your-server.workers.dev', spellcheck: 'false' }),
        'The address of your Chrono server.'),
      field('Upload key', key, 'The secret your server checks before it accepts a clip. Ask whoever set the server up.'),
      field('Your name', textInput(config.Username, (v) => { config.Username = v; }, { maxlength: 32 }),
        'Shown on links you share. Only letters, numbers, - and _ are kept.'),
    ];
  }

  function appSection() {
    return [
      h('h2', { text: 'App' }),
      toggle('Start Chrono with Windows', 'Chrono runs quietly in the tray and records games on its own. There is no overlay.', config.StartWithWindows !== false, (v) => { config.StartWithWindows = v; }),
      toggle('Show notifications', 'A small message when a clip is saved or something needs your attention.', config.ShowNotifications !== false, (v) => { config.ShowNotifications = v; }),
      h('div', { class: 'field', style: { marginTop: '22px' } }, h('label', { text: 'Clips folder' }),
        h('div', { class: 'selectable', style: { marginBottom: '8px', overflowWrap: 'anywhere' }, text: config.OutputFolder }),
        h('button', { class: 'btn', type: 'button', onClick: () => bridge.request('openClipsFolder').catch(() => {}) }, icon('folder'), 'Open folder')),
    ];
  }

  const BUILDERS = { recording: recordingSection, audio: audioSection, hotkeys: hotkeysSection, upload: uploadSection, app: appSection };

  // ------------------------------------------------------------------- frame

  function touched(repaintBar = true) { paintBar(); }

  function invalidReason() {
    const combos = config.Hotkeys.map((k) => Chrono.hotkeyParts(k).join('+'));
    if (config.Hotkeys.some((k) => !k.Key)) return 'Every hotkey needs keys.';
    if (config.Hotkeys.some((k) => !String(k.Name || '').trim())) return 'Every hotkey needs a name.';
    if (config.Hotkeys.some((k) => !(k.ClipLengthSeconds >= 5))) return 'A clip needs at least 5 seconds.';
    if (new Set(combos).size !== combos.length) return 'Two hotkeys use the same keys.';
    return null;
  }

  function paintBar() {
    if (!barHost) return;
    barHost.textContent = '';
    if (!dirty()) return;
    const problem = invalidReason();
    barHost.append(h('div', { class: 'unsaved', role: 'region', 'aria-label': 'Unsaved changes' },
      h('span', { text: problem || 'You have unsaved changes.' }),
      h('button', { class: 'btn ghost', type: 'button', onClick: revert }, 'Reset'),
      h('button', { class: 'btn good', type: 'button', disabled: problem ? true : null, onClick: save }, 'Save changes')));
  }

  function revert() { config = JSON.parse(saved); render(); }

  async function save() {
    try {
      const result = await bridge.request('saveSettings', { config });
      config = result.config;
      saved = JSON.stringify(config);
      Chrono.toast('good', 'Settings saved');
      loadRecommended();
      render();
    } catch (err) {
      Chrono.toast('error', "Couldn't save settings", err.message);
    }
  }

  async function loadRecommended() {
    try {
      recommended = await bridge.request('getRecommendedBitrate', { fps: config.Fps || 60 });
      const box = body && body.querySelector('input[data-hint]');
      if (box) box.placeholder = `${recommended.kbps} (recommended)`;
      hintPainter();
    } catch { /* optional */ }
  }

  function render() {
    if (!body) return;
    stopListening();
    nav.textContent = '';
    nav.append(h('div', { class: 'group', text: 'Chrono' }),
      ...SECTIONS.map(([key, label]) => h('button', { type: 'button', 'aria-current': String(key === section), onClick: () => { section = key; render(); } }, label)));
    body.textContent = '';
    body.append(...BUILDERS[section]());
    paintBar();
  }

  Chrono.register('settings', {
    async mount(container, arg) {
      if (arg && BUILDERS[arg]) section = arg;
      nav = h('nav', { class: 'settings-nav', 'aria-label': 'Settings sections' });
      body = h('div', { class: 'settings-body' });
      barHost = h('div');
      container.append(
        h('div', { class: 'topbar' }, h('span', { class: 'hash', text: '#' }), h('h1', { text: 'Settings' })),
        h('div', { class: 'settings' }, nav, h('div', { class: 'scroll' }, body)), barHost);
      try {
        const data = await bridge.request('getSettings');
        config = data.config; recommended = data.recommended; saved = JSON.stringify(config);
        config.Hotkeys = config.Hotkeys || [];
        saved = JSON.stringify(config);
        render();
      } catch (err) {
        body.append(h('div', { class: 'empty' }, h('h2', { text: "Couldn't load settings" }), h('p', { text: err.message })));
      }
    },
    unmount() { stopListening(); body = nav = barHost = null; },
  });
})(window);
