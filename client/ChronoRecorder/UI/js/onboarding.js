// First-run setup, laid over the app on a new install: username, server address, upload key. Every step can be skipped,
// because a friend may not have a server or a key yet. Nothing is saved until the last step, in one request.
(function (root) {
  const Chrono = root.Chrono;
  const { h, bridge } = Chrono;

  const STEPS = [
    {
      id: 'name',
      title: 'Welcome to Chrono',
      body: 'Chrono records your games in the background. Press a hotkey and it saves the last few seconds as a clip. What should we call you?',
      label: 'Username',
      hint: 'Shown as the owner of links you share. Only letters, numbers, - and _ are kept.',
      attrs: { maxlength: 32, autocomplete: 'off', spellcheck: 'false', placeholder: 'username' },
      next: 'Continue',
      skip: 'Skip',
    },
    {
      id: 'server',
      title: 'Where should clips go?',
      body: 'To share a clip as a link that plays in Discord, Chrono uploads it to a server. If someone gave you a server address, enter it here. Nothing is uploaded unless you press Upload on a clip.',
      label: 'Server address',
      hint: 'You can also set this up later in Settings > Uploading.',
      attrs: { autocomplete: 'off', spellcheck: 'false', placeholder: 'https://your-server.workers.dev' },
      next: 'Continue',
      skip: 'Skip for now',
      required: true,
    },
    {
      id: 'key',
      title: 'Upload key',
      body: 'Your server asks for a key before it accepts a clip. Whoever gave you the server address can give you the key.',
      label: 'Upload key',
      hint: "If you don't have it yet, skip this. You can add it later in Settings > Uploading.",
      attrs: { type: 'password', autocomplete: 'off', spellcheck: 'false', 'aria-label': 'Upload key' },
      next: 'Finish',
      skip: 'Skip for now',
      required: true,
    },
  ];

  const FIELD = { name: 'username', server: 'apiUrl', key: 'uploadKey' };

  let overlay = null;

  function close() {
    if (!overlay) return;
    overlay.remove();
    overlay = null;
    const app = root.document.getElementById('app');
    if (app) app.removeAttribute('inert');
  }

  Chrono.onboarding = {
    /** Shows the setup. Resolves when it is finished or skipped and what was entered has been saved. */
    async start() {
      if (overlay) return;

      const values = { name: '', server: '', key: '' };
      try {
        const { config } = await bridge.request('getSettings');
        if (config.Username && config.Username !== 'username') values.name = config.Username;
        values.server = config.ApiUrl || '';
        values.key = config.UploadKey || '';
      } catch { /* start blank */ }

      const app = root.document.getElementById('app');
      if (app) app.setAttribute('inert', '');
      overlay = h('div', { class: 'onboarding', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': 'onboarding-title' });
      root.document.body.append(overlay);

      return new Promise((resolve) => {
        let index = 0;
        let saving = false;

        async function finish() {
          if (saving) return;
          saving = true;
          const payload = {};
          for (const step of STEPS) if (values[step.id].trim()) payload[FIELD[step.id]] = values[step.id].trim();
          try {
            const result = await bridge.request('completeOnboarding', payload);
            close();
            if (result && result.canUpload) Chrono.toast('good', "You're all set", 'Uploads are ready. Press a hotkey in a game to save a clip.');
            else Chrono.toast('good', "You're all set", 'Press a hotkey in a game to save a clip. You can set up uploading later in Settings.');
            resolve();
          } catch (err) {
            saving = false;
            paint(err.message);
          }
        }

        function go(to) {
          // The key is only any use with a server, so skipping the server ends the setup.
          if (to >= STEPS.length) { finish(); return; }
          index = Math.max(0, to);
          paint();
        }

        function paint(error) {
          const step = STEPS[index];
          const input = h('input', { class: 'input', type: 'text', id: 'onboarding-input', value: values[step.id], ...step.attrs });
          const problem = h('div', { class: 'hint warn', role: 'alert', text: error || '' });
          const next = h('button', { class: 'btn primary big', type: 'submit', text: step.next });

          const check = () => {
            values[step.id] = input.value;
            next.disabled = saving || (step.required && !input.value.trim());
          };
          input.addEventListener('input', () => { problem.textContent = ''; check(); });
          check();

          const form = h('form', {
            class: 'onboarding-card', novalidate: true,
            onSubmit: (e) => {
              e.preventDefault();
              if (next.disabled) return;
              if (step.id === 'server') {
                const why = Chrono.serverAddressProblem(input.value);
                if (why) { problem.textContent = why; input.focus(); return; }
              }
              go(index + 1);
            },
          },
          h('div', { class: 'onboarding-head' },
            h('img', { class: 'onboarding-logo', src: 'img/logo.png', alt: '' }),
            h('div', { class: 'onboarding-progress', role: 'img', 'aria-label': `Step ${index + 1} of ${STEPS.length}` },
              STEPS.map((s, i) => h('i', { class: i <= index ? 'on' : '' })))),
          h('h1', { id: 'onboarding-title', text: step.title }),
          h('p', { class: 'onboarding-body', text: step.body }),
          h('div', { class: 'field' }, h('label', { for: 'onboarding-input', text: step.label }), input,
            h('div', { class: 'hint', text: step.hint }), problem),
          h('div', { class: 'onboarding-actions' },
            index > 0 ? h('button', { class: 'btn ghost', type: 'button', onClick: () => go(index - 1), text: 'Back' }) : null,
            h('span', { class: 'grow' }),
            h('button', { class: 'btn ghost', type: 'button', text: step.skip, onClick: () => {
              // A skipped step saves nothing, and skipping the server skips the key with it.
              values[step.id] = '';
              if (step.id === 'server') { values.key = ''; go(STEPS.length); } else go(index + 1);
            } }),
            next));

          overlay.textContent = '';
          overlay.append(form);
          input.focus();
        }

        paint();
      });
    },
  };
})(window);
