// The clip view: opens over the library when you click a clip. Rename it (a clear title box), trim the left and
// right ends on a timeline made of the clip's own frames, upload it, copy its link, or delete it.
(function (root) {
  const Chrono = root.Chrono;
  const { h, icon, bridge } = Chrono;

  let current = null;   // the open editor, so only one exists at a time

  async function open(id) {
    if (current) current.close();
    let clip;
    try { clip = (await bridge.request('getClip', { id })).clip; }
    catch (err) { Chrono.toast('error', "Couldn't open the clip", err.message); return; }
    current = createEditor(clip);
  }

  function createEditor(initialClip) {
    let clip = initialClip;
    let duration = clip.duration || 0;
    let start = 0;
    let end = duration;
    let busy = false;
    let raf = 0;
    let dragging = null;     // 'start' | 'end' | 'scrub'
    let grab = 0;
    let savedTimer = 0;
    const offs = [];

    // ---------------------------------------------------------------- elements
    const titleInput = h('input', {
      type: 'text', maxlength: 100, value: clip.title, placeholder: 'Give your clip a title', id: 'clip-title',
      'aria-describedby': 'title-saved', spellcheck: 'false',
    });
    const savedMark = h('span', { class: 'saved', id: 'title-saved' }, icon('check'), 'Saved');

    const video = h('video', { preload: 'metadata', playsinline: true });
    const bigPlay = h('span', { class: 'big-play' }, icon('play'));
    const busyMessage = h('span');
    const busyOverlay = h('div', { class: 'busy', role: 'status' }, h('span', { class: 'spinner' }), busyMessage);
    const player = h('div', { class: 'player' }, video, h('div', { class: 'click', onClick: togglePlay, onDblclick: toggleFullscreen }), bigPlay, busyOverlay);

    const playBtn = h('button', { class: 'icon-btn', 'aria-label': 'Play', title: 'Play or pause (space)', onClick: togglePlay }, icon('play'));
    const timeText = h('span', { class: 'time', text: '0:00.0 / 0:00.0' });
    const fullscreenBtn = h('button', { class: 'icon-btn', 'aria-label': 'Full screen', title: 'Full screen (F). Esc to leave.', onClick: toggleFullscreen }, icon('expand'));
    // Playback volume, remembered between clips (per viewer, so it is fine if the browser won't store it).
    const readVolume = () => { try { const v = parseFloat(root.localStorage.getItem('chrono.volume')); return isFinite(v) ? Chrono.clamp(v, 0, 1) : 1; } catch { return 1; } };
    const saveVolume = (v) => { try { root.localStorage.setItem('chrono.volume', String(v)); } catch { /* not remembered */ } };
    let lastVolume = readVolume() || 1;
    video.volume = readVolume();

    const volumeSlider = h('input', { type: 'range', min: 0, max: 100, step: 1, class: 'slider volume', 'aria-label': 'Volume' });
    const volumeText = h('span', { class: 'volume-text' });
    const muteBtn = h('button', { class: 'icon-btn', 'aria-label': 'Mute', onClick: () => {
      if (video.muted || video.volume === 0) { video.muted = false; video.volume = lastVolume; } else { lastVolume = video.volume; video.muted = true; }
      paintMute();
    } }, icon('volume'));

    const strip = h('div', { class: 'strip' });
    const dimLeft = h('div', { class: 'dim left' });
    const dimRight = h('div', { class: 'dim right' });
    const sel = h('div', { class: 'sel' });
    const playhead = h('div', { class: 'playhead' });
    const handleStart = h('div', { class: 'handle left', role: 'slider', tabindex: 0, 'aria-label': 'Trim start', 'aria-valuemin': 0 });
    const handleEnd = h('div', { class: 'handle right', role: 'slider', tabindex: 0, 'aria-label': 'Trim end', 'aria-valuemin': 0 });
    const timeline = h('div', { class: 'timeline' }, strip, dimLeft, dimRight, sel, playhead, handleStart, handleEnd);

    const startText = h('span');
    const keepText = h('span', { class: 'keep' });
    const endText = h('span');
    const resetBtn = h('button', { type: 'button', onClick: resetTrim, hidden: true }, 'Reset');
    const readout = h('div', { class: 'trim-readout' }, startText, h('span', {}, keepText, resetBtn), endText);

    const infoRow = h('div', { class: 'info-row' });
    const linkHost = h('div');

    const deleteHost = h('span');
    const showBtn = h('button', { class: 'btn ghost', onClick: () => bridge.request('showInFolder', { id: clip.id }).catch(fail) }, icon('folder'), 'Show in folder');
    const saveTrimBtn = h('button', { class: 'btn primary', onClick: () => saveTrim(false), title: 'Replace this clip with the trimmed version' }, icon('scissors'), 'Save trim');
    const saveCopyBtn = h('button', { class: 'btn', onClick: () => saveTrim(true), title: 'Keep the original and make a new clip' }, 'Save as new clip');
    const mainHost = h('span');

    const closeBtn = h('button', { class: 'icon-btn', 'aria-label': 'Close', title: 'Close (Esc)', onClick: close }, icon('x'));

    const modal = h('div', { class: 'modal', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Clip' },
      h('div', { class: 'modal-head' },
        h('div', { class: 'title-field' }, h('label', { for: 'clip-title', text: 'Title' }), titleInput, savedMark), closeBtn),
      h('div', { class: 'modal-body' },
        player,
        h('div', { class: 'controls' }, playBtn, timeText, h('span', { class: 'spacer' }), muteBtn, volumeSlider, volumeText, fullscreenBtn),
        timeline, readout, infoRow, linkHost),
      h('div', { class: 'modal-foot' }, deleteHost, showBtn, h('span', { class: 'spacer' }), saveCopyBtn, saveTrimBtn, mainHost));

    const overlay = h('div', { class: 'overlay', onMousedown: (e) => { if (e.target === overlay) close(); } }, modal);
    root.document.body.append(overlay);

    // ---------------------------------------------------------------- painting
    function paintMute() {
      const silent = video.muted || video.volume === 0;
      muteBtn.replaceChildren(icon(silent ? 'mute' : 'volume'));
      muteBtn.setAttribute('aria-label', silent ? 'Unmute' : 'Mute');
      const percent = silent ? 0 : Math.round(video.volume * 100);
      volumeSlider.value = percent;
      volumeSlider.style.setProperty('--fill', `${percent}%`);
      volumeText.textContent = `${percent}%`;
    }

    volumeSlider.addEventListener('input', () => {
      const v = parseInt(volumeSlider.value, 10) / 100;
      video.muted = v === 0;
      video.volume = v;
      if (v > 0) lastVolume = v;
      saveVolume(v);
      paintMute();
    });
    video.addEventListener('volumechange', paintMute);

    function paintPlay() {
      const playing = !video.paused && !video.ended;
      playBtn.replaceChildren(icon(playing ? 'pause' : 'play'));
      playBtn.setAttribute('aria-label', playing ? 'Pause' : 'Play');
      player.classList.toggle('playing', playing);
    }

    function paintTrim() {
      const pctStart = Chrono.percentAt(start, duration);
      const pctEnd = Chrono.percentAt(end, duration);
      dimLeft.style.width = `${pctStart}%`;
      dimRight.style.width = `${100 - pctEnd}%`;
      sel.style.left = `${pctStart}%`;
      sel.style.width = `${pctEnd - pctStart}%`;
      handleStart.style.left = `${pctStart}%`;
      handleEnd.style.left = `${pctEnd}%`;
      handleStart.setAttribute('aria-valuemax', String(Math.round((end - Chrono.MIN_TRIM) * 10) / 10));
      handleStart.setAttribute('aria-valuenow', String(Math.round(start * 10) / 10));
      handleStart.setAttribute('aria-valuetext', Chrono.formatPrecise(start));
      handleEnd.setAttribute('aria-valuemax', String(Math.round(duration * 10) / 10));
      handleEnd.setAttribute('aria-valuenow', String(Math.round(end * 10) / 10));
      handleEnd.setAttribute('aria-valuetext', Chrono.formatPrecise(end));

      startText.textContent = `Start ${Chrono.formatPrecise(start)}`;
      endText.textContent = `End ${Chrono.formatPrecise(end)}`;
      const keep = end - start;
      keepText.textContent = '';
      keepText.append(`Keeping ${Chrono.formatPrecise(keep)}`, h('small', { text: `of ${Chrono.formatPrecise(duration)}` }));

      const trimmed = Chrono.isTrimmed(start, end, duration);
      resetBtn.hidden = !trimmed;
      saveTrimBtn.hidden = !trimmed || !!clip.link;   // an uploaded clip is only ever trimmed into a copy
      saveCopyBtn.hidden = !trimmed;
      saveTrimBtn.disabled = saveCopyBtn.disabled = busy;
      paintTime();
    }

    function paintTime() {
      const t = video.currentTime || 0;
      playhead.style.left = `${Chrono.percentAt(t, duration)}%`;
      playhead.style.display = duration > 0 ? '' : 'none';
      timeText.textContent = `${Chrono.formatPrecise(Math.max(0, t - start))} / ${Chrono.formatPrecise(end - start)}`;
    }

    function paintInfo() {
      infoRow.textContent = '';
      const bits = [
        clip.game ? ['gamepad', clip.game] : null,
        ['clock', Chrono.dateTime(clip.createdUtc)],
        clip.resolution ? ['monitor', clip.resolution.replace('x', '×')] : null,
        clip.sizeBytes ? ['film', Chrono.formatSize(clip.sizeBytes)] : null,
      ].filter(Boolean);
      for (const [ico, text] of bits) infoRow.append(h('span', {}, icon(ico), text));
    }

    function paintShare() {
      linkHost.textContent = '';
      mainHost.textContent = '';
      if (clip.link) {
        const input = h('input', { type: 'text', readonly: true, value: clip.link, 'aria-label': 'Share link', onFocus: (e) => e.target.select() });
        linkHost.append(h('div', { class: 'link-box' }, icon('link'), input,
          h('button', { class: 'btn small', onClick: () => Chrono.actions.copyLink(clip) }, icon('copy'), 'Copy')));
        mainHost.append(h('button', { class: 'btn primary big', onClick: () => Chrono.actions.copyLink(clip) }, icon('link'), 'Copy link'));
        return;
      }

      const label = h('span', { text: 'Upload' });
      const fill = h('span', { class: 'fill' });
      const upload = h('button', { class: 'btn primary big progress', onClick: doUpload }, fill, icon('upload'), label);
      const paint = (fraction) => {
        if (fraction === null || fraction === undefined) { upload.disabled = busy; fill.style.width = '0'; label.textContent = 'Upload'; }
        else { upload.disabled = true; fill.style.width = `${Math.round(fraction * 100)}%`; label.textContent = fraction >= 1 ? 'Finishing' : `Uploading ${Math.round(fraction * 100)}%`; }
      };
      offs.push(Chrono.actions.onProgress(clip.id, paint));
      if (Chrono.actions.isUploading(clip.id)) paint(0);
      mainHost.append(upload);
    }

    function paintDelete(confirming) {
      deleteHost.textContent = '';
      if (!confirming) {
        deleteHost.append(h('button', { class: 'btn ghost', onClick: () => paintDelete(true) }, icon('trash'), 'Delete'));
        return;
      }
      deleteHost.append(
        h('button', { class: 'btn danger', onClick: doDelete }, icon('trash'), 'Move to Recycle Bin'),
        ' ',
        h('button', { class: 'btn ghost', onClick: () => paintDelete(false) }, 'Cancel'));
    }

    function paintAll() {
      paintInfo(); paintShare(); paintTrim(); paintPlay(); paintMute();
    }

    // ------------------------------------------------------------- behaviour
    function fail(err) { Chrono.toast('error', 'Something went wrong', err.message); }

    /** Full screen for the video alone: the browser's own controls take over there (seek bar, volume), and Esc leaves it. */
    function toggleFullscreen() {
      if (busy) return;
      if (root.document.fullscreenElement) root.document.exitFullscreen();
      else if (video.requestFullscreen) video.requestFullscreen().catch(() => {});
    }

    function onFullscreenChange() { video.controls = !!root.document.fullscreenElement; }
    root.document.addEventListener('fullscreenchange', onFullscreenChange);

    function togglePlay() {
      if (busy) return;
      if (video.paused || video.ended) {
        if (video.currentTime < start || video.currentTime >= end - 0.05) video.currentTime = start;
        video.play().catch(() => { /* the user cancelled it by scrubbing */ });
      } else {
        video.pause();
      }
    }

    function tick() {
      raf = requestAnimationFrame(tick);
      if (!video.paused && duration > 0) {
        // Playing plays only what is kept, and stops at its end.
        if (video.currentTime >= end - 0.02) { video.pause(); video.currentTime = start; }
        else if (video.currentTime < start - 0.05) video.currentTime = start;
      }
      paintTime();
    }

    function setDuration(value) {
      if (!isFinite(value) || value <= 0) return;
      const wasFull = end >= duration - 0.05 || duration === 0;
      duration = value;
      if (wasFull) end = duration;
      end = Math.min(end, duration);
      paintTrim();
    }

    function resetTrim() { start = 0; end = duration; video.currentTime = 0; paintTrim(); }

    function rect() { return timeline.getBoundingClientRect(); }

    function beginDrag(kind, e) {
      if (busy) return;
      dragging = kind;
      const r = rect();
      const edge = kind === 'start' ? r.left + (start / duration) * r.width : r.left + (end / duration) * r.width;
      grab = kind === 'scrub' ? 0 : e.clientX - edge;
      e.currentTarget.setPointerCapture && e.currentTarget.setPointerCapture(e.pointerId);
      if (kind !== 'scrub') (kind === 'start' ? handleStart : handleEnd).classList.add('dragging');
      video.pause();
      e.preventDefault();
      moveDrag(e);
    }

    function moveDrag(e) {
      if (!dragging || duration <= 0) return;
      const r = rect();
      const t = Chrono.timeAtX(e.clientX - grab, r.left, r.width, duration);
      if (dragging === 'start') { start = Chrono.moveStart(t, end); video.currentTime = start; }
      else if (dragging === 'end') { end = Chrono.moveEnd(t, start, duration); video.currentTime = Math.max(start, end - 0.05); }
      else { video.currentTime = Chrono.clamp(t, start, end); }
      paintTrim();
    }

    function endDrag() {
      dragging = null;
      handleStart.classList.remove('dragging');
      handleEnd.classList.remove('dragging');
    }

    handleStart.addEventListener('pointerdown', (e) => beginDrag('start', e));
    handleEnd.addEventListener('pointerdown', (e) => beginDrag('end', e));
    timeline.addEventListener('pointerdown', (e) => { if (e.target === timeline || e.target === strip || e.target === sel || e.target.classList.contains('dim')) beginDrag('scrub', e); });
    for (const el of [handleStart, handleEnd, timeline]) {
      el.addEventListener('pointermove', moveDrag);
      el.addEventListener('pointerup', endDrag);
      el.addEventListener('pointercancel', endDrag);
    }
    for (const [el, kind] of [[handleStart, 'start'], [handleEnd, 'end']]) {
      el.addEventListener('keydown', (e) => {
        const step = e.shiftKey ? 1 : 0.1;
        let delta = 0;
        if (e.key === 'ArrowLeft') delta = -step; else if (e.key === 'ArrowRight') delta = step; else return;
        e.preventDefault();
        if (kind === 'start') { start = Chrono.moveStart(start + delta, end); video.currentTime = start; }
        else { end = Chrono.moveEnd(end + delta, start, duration); video.currentTime = Math.max(start, end - 0.05); }
        paintTrim();
      });
    }

    video.addEventListener('loadedmetadata', () => setDuration(video.duration));
    video.addEventListener('play', paintPlay);
    video.addEventListener('pause', paintPlay);
    video.addEventListener('ended', paintPlay);
    video.addEventListener('error', () => Chrono.toast('error', "Couldn't play this clip", 'The video file may be missing or damaged.'));

    // ---- title
    let titleBeforeEdit = clip.title;
    titleInput.addEventListener('focus', () => { titleBeforeEdit = clip.title; });
    titleInput.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); titleInput.blur(); }
      if (e.key === 'Escape') { e.stopPropagation(); titleInput.value = titleBeforeEdit; titleInput.blur(); }
    });
    titleInput.addEventListener('blur', commitTitle);

    async function commitTitle() {
      const wanted = Chrono.cleanTitle(titleInput.value, clip.title);
      titleInput.value = wanted;
      if (wanted === clip.title) return;
      try {
        const result = await bridge.request('renameClip', { id: clip.id, title: wanted });
        clip = result.clip;
        titleInput.value = clip.title;
        savedMark.classList.add('show');
        clearTimeout(savedTimer);
        savedTimer = setTimeout(() => savedMark.classList.remove('show'), 1800);
        if (result.remoteError) Chrono.toast('warn', 'Renamed here, not on the link', `${result.remoteError} The shared page still shows the old title.`);
      } catch (err) {
        titleInput.value = clip.title;
        fail(err);
      }
    }

    // ---- release the file: a playing <video> keeps the file open, and Windows won't replace or delete an open file
    function releaseVideo() {
      // Without a source the video collapses to a small black box; hold the player at its size until the file is back.
      if (player.offsetHeight > 0) player.style.minHeight = `${player.offsetHeight}px`;
      video.pause();
      video.removeAttribute('src');
      video.load();
    }

    /** Cover the player with a message while something is happening to the file (busy stays true meanwhile). */
    function showBusy(text) {
      busyMessage.textContent = text;
      player.classList.add('is-busy');
    }

    function hideBusy() { player.classList.remove('is-busy'); }

    function attachVideo() {
      // A changed file at the same address must not come from the cache (file: URLs, used only in design mode, take no query).
      video.src = clip.videoUrl.startsWith("file:") ? clip.videoUrl : `${clip.videoUrl}?v=${Date.now()}`;
      video.addEventListener('loadeddata', () => { player.style.minHeight = ''; }, { once: true });
      video.load();
    }

    async function loadStrip() {
      try {
        const strip_ = await bridge.request('getFilmstrip', { id: clip.id });
        if (strip_ && strip_.url) strip.style.backgroundImage = `url("${strip_.url}?v=${Date.now()}")`;
      } catch { /* the timeline works without its pictures */ }
    }

    async function saveTrim(asCopy) {
      if (busy || !Chrono.isTrimmed(start, end, duration)) return;
      busy = true; paintTrim();
      releaseVideo();
      showBusy('Trimming your clip…');
      const original = { start, end };
      const label = asCopy ? saveCopyBtn : saveTrimBtn;
      const oldLabel = label.textContent;
      label.textContent = 'Trimming…';
      try {
        const result = await bridge.request('trimClip', { id: clip.id, start: original.start, end: original.end, saveAsCopy: asCopy });
        if (asCopy) {
          Chrono.toast('good', 'Saved as a new clip', result.clip.title);
          close();
          open(result.clip.id);
          return;
        }
        clip = result.clip;
        duration = clip.duration || (original.end - original.start);
        start = 0; end = duration;
        Chrono.toast('good', 'Trim saved', `The clip is now ${Chrono.formatPrecise(duration)} long.`);
        strip.style.backgroundImage = '';
        attachVideo();
        loadStrip();
        paintAll();
      } catch (err) {
        fail(err);
        attachVideo();
      } finally {
        busy = false;
        hideBusy();
        label.textContent = oldLabel;
        if (label === saveTrimBtn) label.prepend(icon('scissors'));
        paintTrim();
      }
    }

    async function doUpload() {
      await commitTitle();
      const updated = await Chrono.actions.upload(clip);
      if (updated) { clip = updated; paintAll(); }
    }

    async function doDelete() {
      busy = true;
      releaseVideo();
      showBusy('Moving to the Recycle Bin…');
      try {
        await bridge.request('deleteClip', { id: clip.id });
        Chrono.toast('good', 'Moved to the Recycle Bin', clip.title);
        close();
      } catch (err) {
        busy = false;
        hideBusy();
        attachVideo();
        paintDelete(false);
        fail(err);
      }
    }

    function onKey(e) {
      if (root.document.fullscreenElement) return;   // in full screen, Esc leaves full screen; it must not also close the clip
      if (e.key === 'Escape') { e.preventDefault(); close(); return; }
      const tag = (root.document.activeElement && root.document.activeElement.tagName) || '';
      if (e.key === ' ' && tag !== 'INPUT' && tag !== 'BUTTON') { e.preventDefault(); togglePlay(); }
      if ((e.key === 'f' || e.key === 'F') && tag !== 'INPUT' && !e.ctrlKey && !e.altKey && !e.metaKey) { e.preventDefault(); toggleFullscreen(); }
    }
    root.document.addEventListener('keydown', onKey);

    offs.push(bridge.on('libraryChanged', async () => {
      if (busy) return;
      try {
        const fresh = (await bridge.request('getClip', { id: clip.id })).clip;
        const changed = fresh.link !== clip.link || fresh.title !== clip.title;
        clip = fresh;
        if (changed) { if (document.activeElement !== titleInput) titleInput.value = clip.title; paintAll(); }
      } catch { close(); }   // the clip is gone
    }));

    function close() {
      if (current !== api) return;
      current = null;
      cancelAnimationFrame(raf);
      root.document.removeEventListener('keydown', onKey);
      root.document.removeEventListener('fullscreenchange', onFullscreenChange);
      if (root.document.fullscreenElement) root.document.exitFullscreen().catch(() => {});
      offs.forEach((off) => off());
      releaseVideo();
      overlay.remove();
    }

    const api = { close };

    // ------------------------------------------------------------------ start
    paintDelete(false);
    paintAll();
    attachVideo();
    loadStrip();
    raf = requestAnimationFrame(tick);
    modal.tabIndex = -1;
    modal.focus();
    return api;
  }

  Chrono.editor = { open };
})(window);
