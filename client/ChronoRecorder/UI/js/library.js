// The library: every clip you have saved. Clips not uploaded yet, a clear divider, then the uploaded ones with a
// conspicuous "Copy link". Clicking a clip opens it in the editor (title, trim, upload, delete).
(function (root) {
  const Chrono = root.Chrono;
  const { h, icon, bridge } = Chrono;

  // ----------------------------------------------------------- shared actions

  const progress = new Map();       // clip id -> 0..1 while it uploads
  const progressListeners = new Map();

  bridge.on('uploadProgress', ({ id, fraction }) => {
    progress.set(id, fraction);
    for (const fn of progressListeners.get(id) || []) fn(fraction);
  });

  const actions = {
    isUploading: (id) => progress.has(id),
    onProgress(id, fn) {
      if (!progressListeners.has(id)) progressListeners.set(id, new Set());
      progressListeners.get(id).add(fn);
      return () => progressListeners.get(id).delete(fn);
    },

    async copyLink(clip) {
      try {
        await bridge.request('copyLink', { id: clip.id });
        Chrono.toast('good', 'Link copied', clip.link);
      } catch (err) {
        Chrono.toast('error', "Couldn't copy the link", err.message);
      }
    },

    /** Upload a clip, keeping the link on the clipboard when it is done. Resolves with the updated clip, or null. */
    async upload(clip) {
      if (progress.has(clip.id)) return null;
      const status = Chrono.state.status;
      if (status && !status.canUpload) {
        Chrono.toast('warn', 'Uploading isn’t set up yet', 'Add your server address and upload key in Settings.',
          { label: 'Open settings', onClick: () => Chrono.nav.go('settings', 'upload') });
        return null;
      }

      progress.set(clip.id, 0);
      for (const fn of progressListeners.get(clip.id) || []) fn(0);
      try {
        const result = await bridge.request('uploadClip', { id: clip.id });
        Chrono.toast('good', 'Uploaded. Link copied', result.clip.link);
        return result.clip;
      } catch (err) {
        Chrono.toast('error', 'Upload failed', `${err.message} Your clip is still saved.`);
        return null;
      } finally {
        progress.delete(clip.id);
        for (const fn of progressListeners.get(clip.id) || []) fn(null);
      }
    },
  };

  Chrono.actions = actions;

  // ------------------------------------------------------------------- cards

  let observer;

  function loadPoster(el) {
    const id = el.dataset.id;
    bridge.request('getPoster', { id }).then((r) => {
      if (r && r.url) el.style.backgroundImage = `url("${r.url}")`;
    }).catch(() => { /* a card without a picture is fine */ });
  }

  function keyLabel(key) { return key === 'Control' ? 'Ctrl' : key; }

  function actionButton(clip) {
    if (clip.link) {
      return h('button', { class: 'btn primary block', onClick: (e) => { e.stopPropagation(); actions.copyLink(clip); } },
        icon('link'), 'Copy link');
    }
    const label = h('span', { text: 'Upload' });
    const fill = h('span', { class: 'fill' });
    const button = h('button', { class: 'btn outline block progress', onClick: async (e) => {
      e.stopPropagation();
      await actions.upload(clip);
    } }, fill, icon('upload'), label);

    const paint = (fraction) => {
      if (fraction === null || fraction === undefined) {
        button.disabled = false; fill.style.width = '0'; label.textContent = 'Upload';
      } else {
        button.disabled = true; fill.style.width = `${Math.round(fraction * 100)}%`;
        label.textContent = fraction >= 1 ? 'Finishing' : `Uploading ${Math.round(fraction * 100)}%`;
      }
    };
    if (actions.isUploading(clip.id)) paint(progress.get(clip.id));
    actions.onProgress(clip.id, paint);
    return button;
  }

  function card(clip) {
    const poster = h('div', { class: 'poster', dataset: { id: clip.id } },
      clip.link ? h('span', { class: 'badge uploaded' }, icon('check'), 'Uploaded') : null,
      clip.duration ? h('span', { class: 'badge duration', text: Chrono.formatDuration(clip.duration) }) : null,
      h('span', { class: 'play' }, h('span', {}, icon('play'))));
    observer.observe(poster);

    const meta = [clip.game, Chrono.timeAgo(clip.createdUtc), Chrono.formatSize(clip.sizeBytes)].filter(Boolean);
    const open = () => Chrono.editor.open(clip.id);

    return h('article', {
      class: `card ${clip.link ? 'is-uploaded' : ''}`, tabindex: 0, role: 'button', 'aria-label': `Open ${clip.title}`,
      onClick: open, onKeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } },
    }, poster,
      h('div', { class: 'card-body' },
        h('div', { class: 'card-title', text: clip.title }),
        h('div', { class: 'card-meta' }, meta.map((m) => h('span', { text: m }))),
        h('div', { class: 'card-actions' }, actionButton(clip))));
  }

  function rule(kind, label, iconName, count) {
    return h('div', { class: `section-rule ${kind}` }, h('span', { class: 'pill' }, icon(iconName), label, ' ', String(count)));
  }

  function emptyState(status) {
    const hotkeys = (status && status.hotkeys) || [];
    const first = hotkeys[0];
    return h('div', { class: 'empty' },
      h('h2', { text: 'No clips yet' }),
      first
        ? h('p', {}, 'While a game is running, press ',
            h('span', { class: 'keys' }, first.keys.map((k) => h('span', { class: 'key', text: keyLabel(k) }))),
            ` to keep the last ${Chrono.plainDuration(first.seconds)}.`)
        : h('p', { text: 'Add a hotkey in Settings to start saving clips.' }),
      h('p', { text: 'Clips you save show up here. Open one to trim it, rename it, or upload it and copy a link for Discord.' }));
  }

  // -------------------------------------------------------------------- page

  let clips = [], query = '', listEl, subEl, offs = [];

  function render() {
    listEl.textContent = '';
    const { local, uploaded } = Chrono.groupClips(clips, query);

    if (clips.length === 0) { listEl.append(emptyState(Chrono.state.status)); return; }
    if (local.length === 0 && uploaded.length === 0) {
      listEl.append(h('div', { class: 'empty' }, h('h2', { text: 'No matches' }), h('p', { text: `No clip is called “${query}” or recorded from a game with that name.` })));
      return;
    }

    if (local.length) {
      listEl.append(rule('local', 'On this PC', 'monitor', local.length),
        h('p', { class: 'section-note', text: 'Only you can see these. Open a clip to trim it, rename it, or upload it.' }),
        h('div', { class: 'grid' }, local.map(card)));
    }
    if (uploaded.length) {
      listEl.append(rule('uploaded', 'Uploaded', 'cloud', uploaded.length),
        h('p', { class: 'section-note', text: 'Anyone with the link can watch these.' }),
        h('div', { class: 'grid' }, uploaded.map(card)));
    }
  }

  async function load() {
    try {
      const data = await bridge.request('getLibrary');
      clips = data.clips;
      subEl.textContent = clips.length ? `${clips.length} clip${clips.length === 1 ? '' : 's'}` : '';
      Chrono.nav.setCount('library', clips.length);
      render();
    } catch (err) {
      listEl.textContent = '';
      listEl.append(h('div', { class: 'empty' }, h('h2', { text: "Couldn't load your clips" }), h('p', { text: err.message })));
    }
  }

  Chrono.register('library', {
    mount(container) {
      observer = new IntersectionObserver((entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) { loadPoster(entry.target); observer.unobserve(entry.target); }
        }
      }, { rootMargin: '200px' });

      subEl = h('span', { class: 'sub' });
      const search = h('input', {
        type: 'search', placeholder: 'Search clips', 'aria-label': 'Search clips',
        onInput: (e) => { query = e.target.value; render(); },
      });
      query = '';
      listEl = h('div', { class: 'page' });

      container.append(
        h('div', { class: 'topbar' }, h('span', { class: 'hash', text: '#' }), h('h1', { text: 'Library' }), subEl,
          h('span', { class: 'spacer' }), h('div', { class: 'search' }, search, icon('search'))),
        h('div', { class: 'scroll' }, listEl));

      offs = [bridge.on('libraryChanged', load)];
      load();
    },
    unmount() {
      offs.forEach((off) => off());
      if (observer) observer.disconnect();
    },
    onStatus() { if (clips.length === 0 && listEl) render(); },
  });
})(window);
