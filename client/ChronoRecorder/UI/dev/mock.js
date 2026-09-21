// Development only: stands in for the Chrono app when this page is opened in a plain browser, so the screens can be
// designed and checked without building the app. Does nothing inside the real app (where chrome.webview exists).
//
// Query options:  ?state=recording|idle|paused|empty   ?open=<clip id>   ?video=<url of a video to play>
// Optional sample pictures go in dev/samples/ (poster1.jpg ... and strip.jpg); without them, gradients are used.
(function (root) {
  const Chrono = root.Chrono;
  if (!Chrono || !Chrono.bridge.isMock) return;

  const params = new URLSearchParams(root.location.search);
  const now = Date.now();
  const minutes = (n) => new Date(now - n * 60000).toISOString();

  const GAMES = ['Risk of rain 2', 'Deep Rock Galactic', 'Deadlock', 'VALORANT'];
  let clips = params.get('state') === 'empty' ? [] : [
    { id: 'c1', title: 'Triple kill on the boss', game: GAMES[0], createdUtc: minutes(4), duration: 20.4, sizeBytes: 34500000, resolution: '2560x1440' },
    { id: 'c2', title: 'Risk of rain 2 - Sep 20, 8:41 PM', game: GAMES[0], createdUtc: minutes(50), duration: 62, sizeBytes: 71200000, resolution: '2560x1440' },
    { id: 'c3', title: 'Did that just happen', game: GAMES[1], createdUtc: minutes(60 * 20), duration: 31.7, sizeBytes: 21000000, resolution: '1920x1080' },
    { id: 'c4', title: 'Quick Clip', game: null, createdUtc: minutes(60 * 26), duration: 28.9, sizeBytes: 18100000, resolution: '2560x1440' },
    { id: 'c5', title: 'Ace with the sheriff, no scope', game: GAMES[3], createdUtc: minutes(60 * 30), duration: 44, sizeBytes: 52000000, resolution: '1920x1080', link: 'https://chrono-clips.example.workers.dev/watch/k3p9Zt2mQxa1', uploadedUtc: minutes(60 * 29), canRemoveUpload: true },
    { id: 'c6', title: 'The vent glitch that took the whole lobby', game: GAMES[2], createdUtc: minutes(60 * 72), duration: 19, sizeBytes: 16000000, resolution: '1920x1080', link: 'https://chrono-clips.example.workers.dev/watch/w8Nq4vB7yLe0', uploadedUtc: minutes(60 * 71), canRemoveUpload: true },
    { id: 'c7', title: 'Clutch 1v4', game: GAMES[3], createdUtc: minutes(60 * 24 * 9), duration: 57, sizeBytes: 60100000, resolution: '1920x1080', link: 'https://chrono-clips.example.workers.dev/watch/p2Xc6hD1sRu5', uploadedUtc: minutes(60 * 24 * 9) },
  ].map((c) => ({ game: null, link: null, uploadedUtc: null, canRemoveUpload: false, videoUrl: params.get('video') || '', fileName: `${c.id}.mp4`, ...c }));

  const status = {
    enabled: true, recording: true, state: 'recording', headline: 'Recording Risk of rain 2', target: 'Risk of rain 2', mode: 'Auto',
    selectedApplication: '', screen: '2560x1440', fps: 60, clipQuality: '1080p60', audio: 'Game sound and microphone', bufferSeconds: 140,
    username: 'Oliver', canUpload: true, showDiagnostics: false, version: '1.1.3',
    hotkeys: [
      { name: 'Quick Clip', keys: ['Control', 'PageUp'], seconds: 30 },
      { name: 'Long Clip', keys: ['Control', 'PageDown'], seconds: 120 },
    ],
  };

  const state = params.get('state');
  if (state === 'idle') Object.assign(status, { recording: false, state: 'idle', headline: 'Waiting for a game' });
  if (state === 'paused') Object.assign(status, { enabled: false, recording: false, state: 'paused', headline: 'Paused' });

  const config = {
    Username: 'Oliver', ApiUrl: 'https://chrono-clips.example.workers.dev', UploadKey: 'secret', Bitrate: 0, Fps: 60, Resolution: '1920x1080',
    Encoder: 'auto', Mode: 2, RecorderEnabled: true, SelectedApplication: '', OutputFolder: 'C:\\Users\\Player\\Videos\\Chrono',
    TempFolder: 'C:\\Temp\\Chrono', RecordAudio: true, RecordMicrophone: true, AudioDelayMs: 0,
    GameCapture: 'auto', SpeakerDeviceId: '', MicrophoneDeviceId: '', MicrophoneVolumePercent: 100, PlaySoundOnClip: true, ShowNotifications: true, StartWithWindows: true,
    FirstRunCompleted: true, ShowDiagnostics: false,
    Hotkeys: [
      { Name: 'Quick Clip', Key: 'PageUp', Modifiers: ['Control'], ClipLengthSeconds: 30 },
      { Name: 'Long Clip', Key: 'PageDown', Modifiers: ['Control'], ClipLengthSeconds: 120 },
    ],
  };

  let meterTimer = 0;
  const wait = (ms) => new Promise((r) => setTimeout(r, ms));
  const find = (id) => clips.find((c) => c.id === id);
  const copy = (c) => JSON.parse(JSON.stringify(c));

  // Gradient "screenshots" so the cards have something to show.
  function gradient(seed) {
    const canvas = root.document.createElement('canvas');
    canvas.width = 480; canvas.height = 270;
    const g = canvas.getContext('2d');
    const hue = (seed * 67) % 360;
    const back = g.createLinearGradient(0, 0, 480, 270);
    back.addColorStop(0, `hsl(${hue} 45% 22%)`); back.addColorStop(1, `hsl(${(hue + 50) % 360} 55% 10%)`);
    g.fillStyle = back; g.fillRect(0, 0, 480, 270);
    g.fillStyle = `hsla(${(hue + 30) % 360} 70% 60% / .35)`;
    g.beginPath(); g.arc(120 + seed * 30 % 240, 150, 60, 0, 7); g.fill();
    g.fillStyle = 'rgba(0,0,0,.35)'; g.fillRect(0, 210, 480, 60);
    return canvas.toDataURL('image/jpeg', 0.8);
  }

  function sample(name, fallback) {
    return new Promise((resolve) => {
      const img = new Image();
      img.onload = () => resolve(`dev/samples/${name}`);
      img.onerror = () => resolve(fallback());
      img.src = `dev/samples/${name}`;
    });
  }

  const handlers = {
    getStatus: async () => copy(status),
    getLibrary: async () => ({ clips: copy(clips), clipsFolder: config.OutputFolder, canUpload: true }),
    getClip: async ({ id }) => { const c = find(id); if (!c) throw new Error('That clip is gone.'); return { clip: copy(c) }; },
    getPoster: async ({ id }) => ({ url: await sample(`poster${(clips.findIndex((c) => c.id === id) % 3) + 1}.jpg`, () => gradient(clips.findIndex((c) => c.id === id) + 1)) }),
    getFilmstrip: async ({ id }) => {
      const c = find(id);
      const url = await sample('strip.jpg', () => {
        const canvas = root.document.createElement('canvas');
        const frames = Math.round(c.duration / 2);
        canvas.width = frames * 160; canvas.height = 90;
        const g = canvas.getContext('2d');
        for (let i = 0; i < frames; i++) {
          g.fillStyle = `hsl(${(i * 23 + 200) % 360} 40% ${16 + (i * 7) % 18}%)`; g.fillRect(i * 160, 0, 160, 90);
          g.fillStyle = 'rgba(255,255,255,.12)'; g.fillRect(i * 160 + 30 + (i * 13) % 60, 30, 26, 44);
        }
        return canvas.toDataURL('image/jpeg', 0.8);
      });
      return { url, frames: Math.round(c.duration / 2) };
    },
    renameClip: async ({ id, title }) => { await wait(150); const c = find(id); c.title = title.trim() || c.title; return { clip: copy(c) }; },
    deleteClip: async ({ id, removeUpload }) => {
      const c = find(id);
      if (removeUpload && !c.canRemoveUpload) throw new Error('This clip was uploaded by an older version of Chrono, so it can\'t be removed from here.');
      clips = clips.filter((x) => x.id !== id); setTimeout(() => Chrono.bridge.emit('libraryChanged'), 0); return {};
    },
    removeUpload: async ({ id }) => {
      await wait(150);
      const c = find(id);
      if (!c.canRemoveUpload) throw new Error('This clip was uploaded by an older version of Chrono, so it can\'t be removed from here. Whoever runs the server can remove it.');
      c.link = null; c.uploadedUtc = null; c.canRemoveUpload = false;
      setTimeout(() => Chrono.bridge.emit('libraryChanged'), 0);
      return { clip: copy(c) };
    },
    copyLink: async () => ({}),
    showInFolder: async () => ({}),
    openClipsFolder: async () => ({}),
    uploadClip: async ({ id }) => {
      for (let i = 1; i <= 10; i++) { await wait(180); Chrono.bridge.emit('uploadProgress', { id, fraction: i / 10 }); }
      const c = find(id); c.link = `https://chrono-clips.example.workers.dev/watch/${id}Zx9q`; c.uploadedUtc = new Date().toISOString(); c.canRemoveUpload = true;
      setTimeout(() => Chrono.bridge.emit('libraryChanged'), 0);
      return { clip: copy(c) };
    },
    trimClip: async ({ id, start, end, saveAsCopy }) => {
      await wait(1200);
      const c = find(id);
      if (saveAsCopy) {
        const n = { ...copy(c), id: `t${Date.now()}`, title: `${c.title} (trimmed)`, duration: end - start, link: null, createdUtc: new Date().toISOString() };
        clips.unshift(n); setTimeout(() => Chrono.bridge.emit('libraryChanged'), 0); return { clip: copy(n) };
      }
      c.duration = end - start; setTimeout(() => Chrono.bridge.emit('libraryChanged'), 0); return { clip: copy(c) };
    },
    setRecorderEnabled: async ({ enabled }) => {
      status.enabled = enabled; status.state = enabled ? (status.recording ? 'recording' : 'idle') : 'paused';
      status.headline = enabled ? 'Recording Risk of rain 2' : 'Paused'; Chrono.bridge.emit('status', copy(status)); return copy(status);
    },
    setMode: async ({ mode, app }) => { status.mode = mode; status.selectedApplication = app || ''; Chrono.bridge.emit('status', copy(status)); return copy(status); },
    getAudioDevices: async () => ({
      speakers: [
        { id: 's1', name: 'Default System Speakers (Realtek USB Audio)', isDefault: true },
        { id: 's2', name: 'Speakers (NVIDIA Broadcast)', isDefault: false },
        { id: 's3', name: 'XV271U M3 (NVIDIA High Definition Audio)', isDefault: false },
      ],
      microphones: [
        { id: 'm1', name: 'Microphone (NVIDIA Broadcast)', isDefault: true },
        { id: 'm2', name: 'Microphone (2- Maono ProStudio 2x2 Lite)', isDefault: false },
        { id: 'm3', name: 'Default System Microphone (Realtek USB Audio)', isDefault: false },
      ],
    }),
    startMicMeter: async () => {
      clearInterval(meterTimer);
      let t = 0;
      // A fake voice: bursts of speech at a low level, like a quiet microphone.
      meterTimer = setInterval(() => {
        t += 0.07;
        const speaking = Math.sin(t * 2.1) > -0.2;
        Chrono.bridge.emit('micLevel', { level: speaking ? 0.02 + 0.05 * Math.abs(Math.sin(t * 9)) : 0.002 });
      }, 70);
      return {};
    },
    stopMicMeter: async () => { clearInterval(meterTimer); return {}; },
    getRunningApps: async () => ({ apps: ['Discord', 'Risk of rain 2', 'Spotify'] }),
    getDiagnostics: async () => {
      const wobble = (base, spread) => (base + (Math.random() - 0.5) * spread).toFixed(1);
      return {
        version: '1.0.1', atUtc: new Date().toISOString(),
        sections: [
          { title: 'Recording', rows: [
            { label: 'Status', value: 'Recording Risk of rain 2', tone: '' },
            { label: 'Capture', value: "Windows Graphics Capture (the game's window)", tone: '' },
            { label: 'Encoder', value: 'NVIDIA NVENC H.264', tone: '' },
            { label: 'Input', value: '2560\u00D71440 @ 60', tone: '' },
            { label: 'Bitrate', value: '19.9 Mbps target, 18.4 Mbps measured', tone: '' },
            { label: 'Buffer', value: '2 min 20 sec', tone: '' },
            { label: 'Load', value: 'Normal (automatic)', tone: '' },
          ] },
          { title: 'Performance', rows: [
            { label: 'FFmpeg CPU', value: `${wobble(0.4, 0.2)}% of the processor (${wobble(9, 3)}% of one core)`, tone: 'good' },
            { label: 'FFmpeg RAM', value: '22 MB', tone: '' },
            { label: 'Chrono RAM', value: '68 MB', tone: '' },
            { label: 'GPU video encode', value: '10%', tone: 'good' },
            { label: "GPU 3D (recording's share)", value: '20%', tone: 'good' },
            { label: 'Encoding speed', value: '1.00x real time', tone: 'good' },
            { label: 'Encoded FPS', value: '59.9', tone: '' },
            { label: 'Dropped frames', value: '0', tone: 'good' },
            { label: 'Repeated frames', value: '188 (normal while the game isn\'t drawing)', tone: '' },
          ] },
          { title: 'This PC', rows: [
            { label: 'Chrono', value: '1.0.1, running for 42 min 10 sec', tone: '' },
            { label: 'Windows', value: 'Windows 11 Home (build 26200)', tone: '' },
            { label: 'Processor', value: '13th Gen Intel(R) Core(TM) i7-13700K (24 threads)', tone: '' },
            { label: 'Memory', value: '64 GB', tone: '' },
            { label: 'Graphics', value: 'NVIDIA GeForce RTX 4080, 15.7 GB, driver 576.02', tone: '' },
            { label: 'Screen', value: '2560\u00D71440 on NVIDIA GeForce RTX 4080', tone: '' },
            { label: 'FFmpeg', value: '9.0.2-essentials_build-www.gyan.dev', tone: '' },
            { label: 'Sound', value: 'game sound', tone: '' },
          ] },
        ],
        findings: [{ tone: 'good', text: 'Nothing needs attention.' }],
        events: ['12:00:01 \u25B6 Recording 2560x1440 at 60 FPS via WindowCapture with h264_nvenc (normal load)'],
      };
    },
    copyDiagnostics: async () => ({}),
    openLogsFolder: async () => ({}),
    getSettings: async () => ({ config: copy(config), recommended: { kbps: 11000, size: '2560x1440', fps: config.Fps } }),
    getRecommendedBitrate: async ({ fps }) => ({ kbps: Math.round(2560 * 1440 * fps * 0.09 / 500000) * 500, size: '2560x1440', fps }),
    saveSettings: async ({ config: next }) => {
      const soundRestarted = ['RecordAudio', 'RecordMicrophone', 'SpeakerDeviceId', 'MicrophoneDeviceId', 'MicrophoneVolumePercent'].some((k) => next[k] !== config[k]);
      const captureRestarted = next.GameCapture !== undefined && next.GameCapture !== config.GameCapture;
      const loadRestarted = ['Fps', 'Bitrate', 'Encoder', 'EncoderLoad'].some((k) => next[k] !== undefined && next[k] !== config[k]);
      Object.assign(config, next);
      status.showDiagnostics = !!config.ShowDiagnostics;
      Chrono.bridge.emit('status', copy(status));
      return { config: copy(config), hotkeyProblems: [], soundRestarted, captureRestarted, loadRestarted };
    },
  };

  Chrono.bridge.calls = [];   // what the page asked for, so the tests can check it
  Chrono.bridge.request = async (action, payload) => {
    Chrono.bridge.calls.push({ action, payload: payload || {} });
    const handler = handlers[action];
    if (!handler) throw new Error(`The mock has no "${action}".`);
    return handler(payload || {});
  };

  root.addEventListener('load', () => {
    const open = params.get('open');
    if (open) setTimeout(() => Chrono.editor.open(open), 300);
  });
})(window);
