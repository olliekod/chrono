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

  const DEFAULTS = { ShowDiagnostics: false, EncoderLoad: 'auto', LearnedLoadLevel: 0, GameCapture: 'window', SpeakerDeviceId: '', MicrophoneDeviceId: '', MicrophoneVolumePercent: 100, PlaySoundOnClip: true };

  const dirty = () => config && JSON.stringify(config) !== saved;

  // ------------------------------------------------------------ small controls

  function field(label, control, hint, warn) {
    // A dropdown is named by the label beside it, for screen readers and for anything that looks for it by name.
    if (control && control.tagName === 'SELECT' && !control.hasAttribute('aria-label')) control.setAttribute('aria-label', label);
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
        ['native', 'Native (largest files)'], ['2560x1440', '1440p'], ['1920x1080', '1080p (recommended)'], ['1280x720', '720p (smallest files)'],
      ], (v) => { config.Resolution = v; }),
      'Native keeps your monitor\'s full resolution. Smaller sizes are made on your graphics card right after you save a clip, which takes a few seconds. The game itself is always recorded at full size, so this never slows it down.'),
      field('Frame rate', select(String(config.Fps || 60), [['30', '30 FPS'], ['60', '60 FPS'], ['120', '120 FPS'], ['144', '144 FPS']],
        (v) => { config.Fps = parseInt(v, 10); loadRecommended(); })),
      h('div', { class: 'field' }, h('label', { text: 'Video bitrate (kbps)' }), bitrate, hint),
      field('Capture games as', select(config.GameCapture || 'window', [
        ['window', "The game's own window (recommended)"], ['monitor', 'The whole monitor, only while the game is in front'],
      ], (v) => { config.GameCapture = v; }),
        "Recording the window means nothing else can ever end up in a clip: not Discord, not your desktop, even if you alt-tab or minimize the game. Only switch to the monitor if a game records as a black picture."),
      field('Recording load', select(config.EncoderLoad || 'auto', [
        ['auto', 'Automatic (recommended)'], ['normal', 'Normal'], ['light', 'Light'],
      ], (v) => { config.EncoderLoad = v; }),
        'Automatic starts lighter on modest graphics cards and, if this PC ever can\'t keep up, lowers itself by using a faster encoder setting and then 30 FPS. It tells you when it does. Choose Normal or Light to fix it yourself. Light uses a faster encoder setting with almost the same picture.'),
      field('Encoder', select(config.Encoder || 'auto', [
        ['auto', 'Automatic (best for this PC)'], ['h264_nvenc', 'NVIDIA graphics card'], ['h264_amf', 'AMD graphics card'], ['h264_qsv', 'Intel graphics'], ['libx264', 'Processor (slow)'],
      ], (v) => { config.Encoder = v; }), 'Leave this on Automatic unless clips fail to save. Recording on your processor is much heavier on the game.'),
    ];
  }

  let hintPainter = () => {};

  // ---- Sound: which devices, and how loud the microphone is ----

  let audioDevices = null;   // { speakers: [...], microphones: [...] } once loaded
  let meterOn = false, meterHold = 0, offMeter = null;

  /** A device dropdown: "Windows default" first (naming what that is right now), then every device Windows lists. */
  function deviceSelect(kind, current, onChange) {
    const el = h('select', { class: 'input', 'aria-label': kind === 'speakers' ? 'Speakers for game sound' : 'Microphone' });
    const fill = () => {
      el.textContent = '';
      const list = (audioDevices && audioDevices[kind]) || [];
      const def = list.find((d) => d.isDefault);
      el.append(h('option', { value: '', text: def ? `Windows default: ${def.name}` : 'Windows default' }));
      for (const d of list) el.append(h('option', { value: d.id, text: d.name }));
      if (current && !list.some((d) => d.id === current)) el.append(h('option', { value: current, text: 'A device that is not connected (Windows default is used)' }));
      el.value = current || '';
    };
    fill();
    el.addEventListener('change', () => { onChange(el.value); touched(); if (kind === 'microphones') syncMeter(); });
    el.refill = fill;
    return el;
  }

  function stopMeter() {
    if (offMeter) { offMeter(); offMeter = null; }
    if (meterOn) { meterOn = false; bridge.request('stopMicMeter').catch(() => {}); }
  }

  /** Listen to the chosen microphone while the Sound page is open and the microphone is switched on. */
  async function syncMeter() {
    const bar = body && body.querySelector('.meter');
    if (!bar) return;
    stopMeter();
    if (config.RecordMicrophone !== true) { bar.classList.add('off'); bar.querySelector('.fill').style.width = '0'; return; }
    bar.classList.remove('off');
    try {
      await bridge.request('startMicMeter', { deviceId: config.MicrophoneDeviceId || '' });
      meterOn = true;
      offMeter = bridge.on('micLevel', ({ level }) => paintMeter(level));
    } catch (err) {
      bar.classList.add('off');
      bar.querySelector('.note').textContent = err.message;
    }
  }

  function paintMeter(level) {
    const bar = body && body.querySelector('.meter');
    if (!bar) return;
    const volume = config.MicrophoneVolumePercent === undefined ? 100 : config.MicrophoneVolumePercent;
    const percent = Chrono.levelPercent(level, volume);
    meterHold = Math.max(percent, meterHold - 2.5);   // the marker falls slowly so a peak can be read
    bar.querySelector('.fill').style.width = `${percent}%`;
    bar.querySelector('.hold').style.left = `${meterHold}%`;
    bar.classList.toggle('loud', Chrono.isLoud(level, volume));
    bar.querySelector('.note').textContent = Chrono.isLoud(level, volume)
      ? 'Too loud: it will be squashed by a limiter. Turn the volume down a little.'
      : 'Talk normally. The bar should reach the yellow zone on your loudest words.';
  }

  function audioSection() {
    const speakers = deviceSelect('speakers', config.SpeakerDeviceId, (v) => { config.SpeakerDeviceId = v; });
    const microphone = deviceSelect('microphones', config.MicrophoneDeviceId, (v) => { config.MicrophoneDeviceId = v; });

    if (!audioDevices) {
      bridge.request('getAudioDevices').then((devices) => { audioDevices = devices; speakers.refill(); microphone.refill(); }).catch(() => {});
    }

    const volume = h('input', { type: 'range', min: 0, max: 500, step: 10, 'aria-label': 'Microphone volume', class: 'slider' });
    const volumeText = h('span', { class: 'slider-value' });
    const setVolume = (v) => { config.MicrophoneVolumePercent = v; volumeText.textContent = `${v}%`; volume.style.setProperty('--fill', `${v / 5}%`); };
    volume.value = config.MicrophoneVolumePercent === undefined ? 100 : config.MicrophoneVolumePercent;
    setVolume(parseInt(volume.value, 10));
    volume.addEventListener('input', () => { setVolume(parseInt(volume.value, 10)); touched(false); });

    const meter = h('div', { class: 'meter' },
      h('div', { class: 'track' }, h('div', { class: 'fill' }), h('div', { class: 'hold' })),
      h('div', { class: 'note', text: 'Talk normally. The bar should reach the yellow zone on your loudest words.' }));

    const nodes = [
      h('h2', { text: 'Sound' }),
      h('p', { class: 'h2-note', text: 'Changing sound settings restarts the current recording, so the last few minutes of footage are dropped.' }),
      toggle('Record game sound', 'Everything you hear: the game, Discord voice chat and music.', config.RecordAudio !== false, (v) => { config.RecordAudio = v; }),
      field('Speakers', speakers, 'Pick the speakers or headphones you hear the game through. This is the same list as Windows Sound settings.'),
      toggle('Record my microphone', 'Mix your own voice into clips.', config.RecordMicrophone === true, (v) => { config.RecordMicrophone = v; syncMeter(); }),
      field('Microphone', microphone, 'Choose the microphone to record, for example NVIDIA Broadcast for noise removal.'),
      h('div', { class: 'field' }, h('label', { text: 'Microphone volume' }), h('div', { class: 'slider-row' }, volume, volumeText), meter,
        h('div', { class: 'hint', text: 'Quiet microphone? Raise this. It is applied to clips only, not to your Windows settings. Anything that would go past full scale is limited so it never crackles.' })),
      toggle('Play a sound when a clip is saved', 'A short chime so you know your hotkey worked without looking.', config.PlaySoundOnClip !== false, (v) => { config.PlaySoundOnClip = v; }),
    ];
    setTimeout(syncMeter, 0);   // after the page is on screen
    return nodes;
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
          parts.forEach((p, i) => capture.append(i ? ' + ' : '', h('span', { class: 'key', text: Chrono.hotkeyLabel(p) })));
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
      const problem = key ? Chrono.hotkeyProblem(mods, key) : `Chrono can't use that key. Try a letter, number, punctuation key, arrow, F1 to F24, or a navigation key like PageUp.`;
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
      field('Username', textInput(config.Username, (v) => { config.Username = v; }, { maxlength: 32 }),
        'Shown as the owner of links you share. Only letters, numbers, - and _ are kept.'),
    ];
  }

  function appSection() {
    return [
      h('h2', { text: 'App' }),
      toggle('Start Chrono with Windows', 'Chrono stays in the tray and records games on its own. It has no overlay.', config.StartWithWindows !== false, (v) => { config.StartWithWindows = v; }),
      toggle('Show notifications', 'A small message when a clip is saved or something needs your attention.', config.ShowNotifications !== false, (v) => { config.ShowNotifications = v; }),
      toggle('Show Diagnostics', 'Adds a Diagnostics page to the sidebar with live performance numbers and a report you can copy. Handy when something isn\'t working.', config.ShowDiagnostics === true, (v) => { config.ShowDiagnostics = v; }),
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
      if (result.soundRestarted) Chrono.toast('good', 'Sound settings applied', 'The recording restarted with your new devices and volume.');
      else if (result.captureRestarted) Chrono.toast('good', 'Capture setting applied', 'The recording restarted.');
      else if (result.loadRestarted) Chrono.toast('good', 'Recording settings applied', 'The recording restarted with your new frame rate, quality or load.');
      if (result.hotkeyProblems && result.hotkeyProblems.length) {
        Chrono.toast('warn', "A hotkey couldn't be set", `Another program already uses these keys: ${result.hotkeyProblems.join(', ')}. Pick different ones.`);
      }
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
    stopMeter();
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
        // Settings added in newer versions may be missing from an older file; fill them in so they don't count as edits.
        for (const [key, value] of Object.entries(DEFAULTS)) if (config[key] === undefined) config[key] = value;
        saved = JSON.stringify(config);
        render();
      } catch (err) {
        body.append(h('div', { class: 'empty' }, h('h2', { text: "Couldn't load settings" }), h('p', { text: err.message })));
      }
    },
    unmount() { stopListening(); stopMeter(); body = nav = barHost = null; },
  });
})(window);
