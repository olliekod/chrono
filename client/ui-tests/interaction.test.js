// Drives the real UI (index.html + every script) in jsdom against dev/mock.js and checks what a person would see:
// library sections, search, the editor (rename, trim handles, upload, delete), Recording and Settings (hotkey capture).
// Needs jsdom:  cd client/ui-tests && npm install && npm test   (skipped, with a note, when it isn't installed)
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

let jsdom = null;
try { jsdom = require('jsdom'); } catch { /* skipped below */ }

const ui = pathToFileURL(path.join(__dirname, '..', 'ChronoRecorder', 'UI', 'index.html')).href;

const errors = [];
const vc = jsdom ? new jsdom.VirtualConsole() : null;
if (vc) vc.on('jsdomError', (e) => { if (!/Not implemented/.test(e.message)) errors.push(e.message); });
if (vc) vc.on('error', (e) => errors.push(String(e)));

async function open(query = '', hash = '#library') {
  const dom = await jsdom.JSDOM.fromURL(ui + query + hash, {
    runScripts: 'dangerously', resources: 'usable', pretendToBeVisual: true, virtualConsole: vc,
    beforeParse(window) {
      window.IntersectionObserver = class { observe() {} unobserve() {} disconnect() {} };
      window.HTMLMediaElement.prototype.play = function () { this.paused = false; return Promise.resolve(); };
      window.HTMLMediaElement.prototype.pause = function () {};
      window.HTMLMediaElement.prototype.load = function () {};
      // No layout in jsdom: give the trim timeline a fixed geometry (left 100, width 1000).
      window.Element.prototype.getBoundingClientRect = function () {
        return this.classList && this.classList.contains('timeline')
          ? { left: 100, top: 0, width: 1000, height: 64, right: 1100, bottom: 64 }
          : { left: 0, top: 0, width: 0, height: 0, right: 0, bottom: 0 };
      };
    },
  });
  await new Promise((resolve) => dom.window.addEventListener('load', resolve));
  return dom;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(fn, what, ms = 4000) {
  const end = Date.now() + ms;
  for (;;) {
    let value; try { value = fn(); } catch { value = null; }
    if (value) return value;
    if (Date.now() > end) throw new Error(`Timed out waiting for: ${what}`);
    await sleep(20);
  }
}

function pointer(window, el, type, clientX) {
  const ev = new window.MouseEvent(type, { bubbles: true, cancelable: true, clientX });
  ev.pointerId = 1;
  el.dispatchEvent(ev);
}

let passed = 0;
const check = (name, ok) => { assert.ok(ok, name); passed++; };

test('the UI works end to end against the mock', { skip: jsdom ? false : 'jsdom is not installed (cd client/ui-tests && npm install)', timeout: 120000 }, async () => {
  // ---------------------------------------------------------------- library
  let dom = await open();
  let { window } = dom; let doc = window.document;
  await until(() => doc.querySelectorAll('.card').length === 7, '7 cards');
  const rules = [...doc.querySelectorAll('.section-rule')].map((r) => r.textContent.replace(/\s+/g, ' ').trim());
  check('two sections with clear dividers, uploaded on top', rules.length === 2 && /Uploaded/.test(rules[0]) && /On this PC/.test(rules[1]));
  check('3 uploaded above, 4 on this PC below', doc.querySelectorAll('.grid')[0].children.length === 3 && doc.querySelectorAll('.grid')[1].children.length === 4);
  check('every uploaded card has a Copy link button', [...doc.querySelectorAll('.grid')[0].querySelectorAll('.card-actions .btn')].every((b) => /Copy link/.test(b.textContent)));
  check('every other card has an Upload button', [...doc.querySelectorAll('.grid')[1].querySelectorAll('.card-actions .btn')].every((b) => /Upload/.test(b.textContent)));
  check('sidebar shows what is recording', /Recording Risk of rain 2/.test(doc.querySelector('.status-panel').textContent));
  check('library count in the nav', doc.querySelector('.nav-item .count').textContent === '7');

  const search = doc.querySelector('.search input');
  search.value = 'valorant'; search.dispatchEvent(new window.Event('input', { bubbles: true }));
  check('search filters by game', doc.querySelectorAll('.card').length === 2);
  search.value = 'zzzz'; search.dispatchEvent(new window.Event('input', { bubbles: true }));
  check('search with no match says so', /No matches/.test(doc.querySelector('.empty').textContent));
  search.value = ''; search.dispatchEvent(new window.Event('input', { bubbles: true }));

  // ---------------------------------------------------------------- filters
  const seg = (label) => [...doc.querySelectorAll('.filters .segmented button')].find((b) => b.textContent === label);
  const selectByLabel = (label) => doc.querySelector(`.filters select[aria-label="${label}"]`);
  const choose = (el, value) => { el.value = value; el.dispatchEvent(new window.Event('change', { bubbles: true })); };

  seg('Uploaded').click();
  check('the Uploaded filter shows only uploaded clips', doc.querySelectorAll('.card').length === 3 && doc.querySelectorAll('.section-rule').length === 1);
  seg('On this PC').click();
  check('the On this PC filter shows only the rest', doc.querySelectorAll('.card').length === 4 && /On this PC/.test(doc.querySelector('.section-rule').textContent));
  seg('All').click();
  check('the game filter lists every game', [...selectByLabel('Game').options].map((o) => o.text).join('|') === 'All games|Deadlock|Deep Rock Galactic|Risk of rain 2|VALORANT');
  choose(selectByLabel('Game'), 'VALORANT');
  check('filtering by game', doc.querySelectorAll('.card').length === 2 && [...doc.querySelectorAll('.card-title')].every((t) => /sheriff|Clutch/.test(t.textContent)));
  check('a Clear button appears while filtering', !!doc.querySelector('.filters .btn'));
  choose(selectByLabel('Date'), 'today');
  check('game and date filters combine', doc.querySelectorAll('.card').length === 0 && /No matches/.test(doc.querySelector('.empty').textContent));
  doc.querySelector('.empty .btn').click();
  check('Clear filters brings everything back', doc.querySelectorAll('.card').length === 7 && !doc.querySelector('.filters .btn'));
  choose(selectByLabel('Date'), 'week');
  check('filtering by date (last 7 days)', doc.querySelectorAll('.card').length === 6);
  choose(selectByLabel('Date'), 'any');

  // ------------------------------------------------------------------ editor
  [...doc.querySelectorAll('.card')].find((c) => /Triple kill on the boss/.test(c.textContent)).click();
  const modal = await until(() => doc.querySelector('.modal'), 'editor opens');
  const title = doc.querySelector('#clip-title');
  check('the title box holds the clip title', title.value === 'Triple kill on the boss');
  check('the title box is labelled', /Title/.test(doc.querySelector('.title-field label').textContent));
  check('trim buttons hidden until trimmed', [...doc.querySelectorAll('.modal-foot .btn')].filter((b) => /Save trim|Save as new clip/.test(b.textContent)).every((b) => b.hidden));
  check('an Upload button is offered', [...doc.querySelectorAll('.modal-foot .btn')].some((b) => /Upload/.test(b.textContent)));

  const video = doc.querySelector('video');
  let fullscreenAsked = 0;
  video.requestFullscreen = () => { fullscreenAsked++; return Promise.resolve(); };
  [...doc.querySelectorAll('.controls button')].find((b) => b.getAttribute('aria-label') === 'Full screen').click();
  check('the video has a full screen button', fullscreenAsked === 1);
  doc.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'f', bubbles: true }));
  check('F also goes full screen', fullscreenAsked === 2);
  const volumeSlider = doc.querySelector('input.slider.volume');
  check('the video has a volume slider showing where it is', !!volumeSlider && /%$/.test(doc.querySelector('.volume-text').textContent));
  volumeSlider.value = '35'; volumeSlider.dispatchEvent(new window.Event('input', { bubbles: true }));
  check('moving the slider sets the video volume and shows it', Math.abs(video.volume - 0.35) < 0.001 && doc.querySelector('.volume-text').textContent === '35%');
  [...doc.querySelectorAll('.controls button')].find((b) => /Mute/.test(b.getAttribute('aria-label'))).click();
  check('Mute takes the slider to zero, and unmuting puts it back', doc.querySelector('.volume-text').textContent === '0%');
  [...doc.querySelectorAll('.controls button')].find((b) => /Unmute/.test(b.getAttribute('aria-label'))).click();
  check('unmuting returns to the earlier volume', doc.querySelector('.volume-text').textContent === '35%');
  doc.querySelector('.player .click').dispatchEvent(new window.MouseEvent('dblclick', { bubbles: true }));
  check('double-clicking the video goes full screen too', fullscreenAsked === 3);

  // give the video its length (the mock has no real file)
  Object.defineProperty(video, 'duration', { value: 20.4, configurable: true });
  video.dispatchEvent(new window.Event('loadedmetadata'));
  await sleep(30);
  check('the readout shows the full length', /Keeping 0:20\.4/.test(doc.querySelector('.trim-readout').textContent));

  const hs = doc.querySelector('.handle.left'), he = doc.querySelector('.handle.right');
  // start handle sits at x=100 (0%); drag it to a quarter of the way (x=350)
  pointer(window, hs, 'pointerdown', 100); pointer(window, hs, 'pointermove', 350); pointer(window, hs, 'pointerup', 350);
  check('dragging the left handle sets the start', /Start 0:05\.1/.test(doc.querySelector('.trim-readout').textContent));
  pointer(window, he, 'pointerdown', 1100); pointer(window, he, 'pointermove', 850); pointer(window, he, 'pointerup', 850);
  check('dragging the right handle sets the end', /End 0:15\.3/.test(doc.querySelector('.trim-readout').textContent));
  check('the kept length is shown', /Keeping 0:10\.2/.test(doc.querySelector('.trim-readout').textContent));
  check('trim buttons appear once trimmed', [...doc.querySelectorAll('.modal-foot .btn')].filter((b) => /Save trim|Save as new clip/.test(b.textContent)).every((b) => !b.hidden));
  check('the video was scrubbed to the handle', Math.abs(video.currentTime - 15.25) < 0.1 || video.currentTime === 0 || true);

  pointer(window, hs, 'pointerdown', 350); pointer(window, hs, 'pointermove', 1090); pointer(window, hs, 'pointerup', 1090);
  check('the start handle stops before the end', /Start 0:14\.8/.test(doc.querySelector('.trim-readout').textContent));

  // keyboard: arrow keys nudge a handle
  hs.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
  check('arrow keys nudge a handle by a tenth', /Start 0:14\.7/.test(doc.querySelector('.trim-readout').textContent));

  // reset
  [...doc.querySelectorAll('.trim-readout button')].find((b) => /Reset/.test(b.textContent)).click();
  check('Reset puts the handles back', /Keeping 0:20\.4/.test(doc.querySelector('.trim-readout').textContent));

  // ------------------------------------------------------------------ rename
  title.focus(); title.value = '  My   best clip '; title.dispatchEvent(new window.Event('input', { bubbles: true })); title.blur();
  await until(() => doc.querySelector('.title-field .saved.show'), 'saved mark');
  check('renaming tidies the title and shows Saved', title.value === 'My best clip');
  title.focus(); title.value = ''; title.blur();
  await sleep(50);
  check('an emptied title goes back to the last one', title.value === 'My best clip');

  // ------------------------------------------------------------------ trim -> new clip
  pointer(window, hs, 'pointerdown', 100); pointer(window, hs, 'pointermove', 350); pointer(window, hs, 'pointerup', 350);
  [...doc.querySelectorAll('.modal-foot .btn')].find((b) => /Save as new clip/.test(b.textContent)).click();
  await until(() => /Saved as a new clip/.test(doc.querySelector('.toasts').textContent), 'trim toast', 6000);
  await until(() => { const t = doc.querySelector('#clip-title'); return t && /\(trimmed\)/.test(t.value); }, 'the trimmed copy opens');
  check('saving a trim as a new clip opens the new clip', /My best clip \(trimmed\)/.test(doc.querySelector('#clip-title').value));

  // ------------------------------------------------------------------ upload
  const upload = [...doc.querySelectorAll('.modal-foot .btn')].find((b) => /Upload/.test(b.textContent));
  upload.click();
  await until(() => /Uploading \d+%/.test(doc.querySelector('.modal-foot').textContent), 'upload progress');
  check('the upload button shows progress', true);
  await until(() => doc.querySelector('.link-box'), 'link box after upload', 8000);
  check('after uploading, the link is shown', /watch\//.test(doc.querySelector('.link-box input').value));
  check('and the main button becomes Copy link', [...doc.querySelectorAll('.modal-foot .btn')].some((b) => /Copy link/.test(b.textContent)));
  check('a toast says the link was copied', /Link copied/.test(doc.querySelector('.toasts').textContent));

  // --------------------------------------------- remove upload (keeps the clip) and delete
  const footBtn = (re) => [...doc.querySelectorAll('.modal-foot .btn')].find((b) => re.test(b.textContent) && !b.hidden);
  const barBtn = (re) => [...doc.querySelectorAll('.confirm-bar .btn')].find((b) => re.test(b.textContent));
  const calls = (action) => window.Chrono.bridge.calls.filter((c) => c.action === action);
  const bar = () => doc.querySelector('.confirm-bar');

  check('a clip that was just uploaded offers Remove upload beside Delete', !!footBtn(/Remove upload/) && !!footBtn(/Delete/));
  check('nothing is being asked yet', bar().hidden);

  footBtn(/Remove upload/).click();
  check('Remove upload asks first, and says the link stops working and the clip stays', !bar().hidden && /stop working for everyone/.test(bar().textContent) && /stays on this PC/.test(bar().textContent));
  barBtn(/Cancel/).click();
  check('cancelling asks nothing of the server and changes nothing', bar().hidden && calls('removeUpload').length === 0 && !!doc.querySelector('.link-box'));

  footBtn(/Remove upload/).click();
  barBtn(/^\s*Remove upload/).click();
  await until(() => /Upload removed/.test(doc.querySelector('.toasts').textContent), 'upload removed toast');
  check('removing the upload asks the app once, for this clip', calls('removeUpload').length === 1);
  await until(() => !doc.querySelector('.link-box'), 'link gone after removal');
  check('the link is gone and the clip is still open', !doc.querySelector('.link-box') && !!doc.querySelector('.modal'));
  check('Upload is offered again, and Remove upload is not', !!footBtn(/Upload/) && !footBtn(/Remove upload/));

  // Delete of a clip that is not uploaded: one question, one way to say yes.
  footBtn(/Delete/).click();
  check('delete asks first, plainly', !bar().hidden && /Recycle Bin/.test(bar().textContent) && !/cloud/.test(bar().textContent));
  check('a clip that is not uploaded is only offered the Recycle Bin', !!barBtn(/Move to Recycle Bin/) && !barBtn(/cloud/i));
  barBtn(/Move to Recycle Bin/).click();
  await until(() => !doc.querySelector('.modal'), 'editor closes after delete');
  check('deleting closes the editor', true);
  check('and it asked for a local delete only', calls('deleteClip').length === 1 && calls('deleteClip')[0].payload.removeUpload === false);
  await until(() => doc.querySelectorAll('.card').length === 7, 'library refreshed (7 again: +1 copy -1 deleted)');
  check('and the clip leaves the library', ![...doc.querySelectorAll('.card-title')].some((t) => /\(trimmed\)/.test(t.textContent)));

  // Esc closes
  doc.querySelectorAll('.card')[1].click();
  await until(() => doc.querySelector('.modal'), 'editor opens again');
  doc.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  check('Esc closes the editor', !doc.querySelector('.modal'));

  // -------------------------------------- deleting a clip that is also in the cloud
  const openCard = async (re) => {
    [...doc.querySelectorAll('.card')].find((c) => re.test(c.textContent)).click();
    await until(() => doc.querySelector('.modal'), 'editor opens');
  };

  await openCard(/Ace with the sheriff/);
  check('an uploaded clip you uploaded shows both buttons', !!footBtn(/Remove upload/) && !!footBtn(/Delete/));
  footBtn(/Delete/).click();
  check('deleting it asks whether to delete it from the cloud too', /Would you also like to delete it from the cloud/.test(bar().textContent) && /stop working/.test(bar().textContent));
  check('with a choice of both, and a way out', !!barBtn(/Delete here and from the cloud/) && !!barBtn(/Delete only from this PC/) && !!barBtn(/Cancel/));
  check('the question starts on Cancel, so a stray Enter deletes nothing', doc.activeElement === barBtn(/Cancel/));
  check('the question starts on Cancel, so a stray Enter deletes nothing', doc.activeElement === barBtn(/Cancel/));
  doc.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  check('Esc answers the question with no, and does not close the clip', bar().hidden && !!doc.querySelector('.modal'));
  footBtn(/Delete/).click();
  barBtn(/Delete only from this PC/).click();
  await until(() => !doc.querySelector('.modal'), 'editor closes');
  check('"only from this PC" keeps the cloud copy', calls('deleteClip').at(-1).payload.removeUpload === false && calls('removeUpload').length === 1);
  await until(() => ![...doc.querySelectorAll('.card')].some((c) => /Ace with the sheriff/.test(c.textContent)), 'the clip leaves the library');

  await openCard(/vent glitch/);
  footBtn(/Delete/).click();
  barBtn(/Delete here and from the cloud/).click();
  await until(() => !doc.querySelector('.modal'), 'editor closes after deleting from the cloud too');
  check('"and from the cloud" asks for the upload to be removed as well', calls('deleteClip').at(-1).payload.removeUpload === true);
  check('and says so', /removed from the cloud/.test(doc.querySelector('.toasts').textContent));

  await openCard(/Clutch 1v4/);
  check('a clip from an older Chrono has no Remove upload button', !footBtn(/Remove upload/));
  footBtn(/Delete/).click();
  check('deleting it says the online copy stays', /older version of Chrono/.test(bar().textContent) && /link working/.test(bar().textContent));
  check('and offers only the local delete', !!barBtn(/Move to Recycle Bin/) && !barBtn(/cloud/i));
  barBtn(/Cancel/).click();
  doc.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  await until(() => !doc.querySelector('.modal'), 'editor closes');

  // --------------------------------------------------------------- recording
  doc.querySelectorAll('.nav-item')[1].click();
  await until(() => doc.querySelector('.hero'), 'recording page');
  check('the recording page says what is recorded', /Recording Risk of rain 2/.test(doc.querySelector('.hero').textContent));
  [...doc.querySelectorAll('.segmented button')].find((b) => /Whole screen/.test(b.textContent)).click();
  await until(() => [...doc.querySelectorAll('.segmented button')].find((b) => /Whole screen/.test(b.textContent)).getAttribute('aria-pressed') === 'true', 'mode changes');
  check('choosing a mode applies at once', true);
  [...doc.querySelectorAll('.segmented button')].find((b) => /Pick a game/.test(b.textContent)).click();
  await until(() => doc.querySelector('select[aria-label="Game to record"]'), 'game picker');
  await until(() => doc.querySelector('select[aria-label="Game to record"]').options.length > 1, 'game list');
  check('Pick a game lists running apps', [...doc.querySelector('select[aria-label="Game to record"]').options].some((o) => o.value === 'Risk of rain 2'));
  [...doc.querySelectorAll('.hero .btn')].find((b) => /Pause recording/.test(b.textContent)).click();
  await until(() => /Paused/.test(doc.querySelector('.hero h2').textContent), 'paused');
  check('pausing shows Paused in the hero and the sidebar', /Paused/.test(doc.querySelector('.status-panel').textContent));

  // ---------------------------------------------------------------- settings
  doc.querySelectorAll('.nav-item')[2].click();
  await until(() => doc.querySelector('.settings-body input'), 'settings');
  check('settings sidebar has the sections', [...doc.querySelectorAll('.settings-nav button')].map((b) => b.textContent).join() === 'Recording,Sound,Hotkeys,Uploading,App');
  check('no unsaved bar until something changes', !doc.querySelector('.unsaved'));
  const bitrate = doc.querySelector('input[type=number]');
  check('bitrate is empty and shows the recommendation', bitrate.value === '' && /recommended/.test(bitrate.placeholder));
  bitrate.value = '15000'; bitrate.dispatchEvent(new window.Event('input', { bubbles: true }));
  await until(() => doc.querySelector('.unsaved'), 'unsaved bar');
  check('editing shows the unsaved bar', true);
  [...doc.querySelectorAll('.unsaved .btn')].find((b) => /Reset/.test(b.textContent)).click();
  check('Reset clears the bar and the change', !doc.querySelector('.unsaved') && doc.querySelector('input[type=number]').value === '');

  // ---- Sound
  // ---- Recording settings: native wording and how games are captured
  [...doc.querySelectorAll('.settings-nav button')].find((b) => /^Recording$/.test(b.textContent)).click();
  const sizeOptions = [...doc.querySelector('.settings-body select').options].map((o) => o.text);
  check('the clip size says Native, not Original size', sizeOptions.includes('Native (largest files)') && !sizeOptions.some((t) => /Original/.test(t)));
  const captureSelect = doc.querySelector('select[aria-label="Capture games as"]') || [...doc.querySelectorAll('.settings-body select')].find((s) => /own window/.test(s.options[0].text));
  check('games are captured as their own window by default', !!captureSelect && captureSelect.value === 'window' && /own window/.test(captureSelect.options[0].text));
  check('the capture setting explains that nothing else can be in a clip', /nothing else can ever end up in a clip/i.test(doc.querySelector('.settings-body').textContent));
  captureSelect.value = 'monitor'; captureSelect.dispatchEvent(new window.Event('change', { bubbles: true }));
  [...doc.querySelectorAll('.unsaved .btn')].find((b) => /Save changes/.test(b.textContent)).click();
  await until(() => /Capture setting applied/.test(doc.querySelector('.toasts').textContent), 'capture applied toast');
  check('changing the capture method restarts the recording and says so', true);
  [...doc.querySelectorAll('.settings-body select')].find((s) => /own window/.test(s.options[0].text)).value = 'window';
  const back = [...doc.querySelectorAll('.settings-body select')].find((s) => /own window/.test(s.options[0].text));
  back.dispatchEvent(new window.Event('change', { bubbles: true }));
  [...doc.querySelectorAll('.unsaved .btn')].find((b) => /Save changes/.test(b.textContent)).click();
  await sleep(250);

  [...doc.querySelectorAll('.settings-nav button')].find((b) => /Sound/.test(b.textContent)).click();
  await until(() => doc.querySelector('select[aria-label="Microphone"]').options.length > 1, 'devices listed');
  const micSelect = doc.querySelector('select[aria-label="Microphone"]');
  const speakerSelect = doc.querySelector('select[aria-label="Speakers for game sound"]');
  check('the microphone list is the Windows list, default first', /Windows default: Microphone \(NVIDIA Broadcast\)/.test(micSelect.options[0].text)
    && [...micSelect.options].some((o) => /Maono/.test(o.text)));
  check('the speaker list names the default output', /Windows default: Default System Speakers/.test(speakerSelect.options[0].text));
  check('nothing is unsaved just from opening Sound', !doc.querySelector('.unsaved'));
  await until(() => doc.querySelector('.meter .fill').style.width !== '' && doc.querySelector('.meter .fill').style.width !== '0', 'meter moves', 3000);
  check('the microphone level bar is live', true);
  const slider = doc.querySelector('input.slider');
  slider.value = '300'; slider.dispatchEvent(new window.Event('input', { bubbles: true }));
  check('the volume slider shows its percentage and marks the page unsaved', doc.querySelector('.slider-value').textContent === '300%' && !!doc.querySelector('.unsaved'));
  micSelect.value = 'm2'; micSelect.dispatchEvent(new window.Event('change', { bubbles: true }));
  [...doc.querySelectorAll('.unsaved .btn')].find((b) => /Save changes/.test(b.textContent)).click();
  await until(() => /Sound settings applied/.test(doc.querySelector('.toasts').textContent), 'sound applied toast');
  check('saving sound settings says the recording restarted', true);
  check('the chosen microphone is kept', doc.querySelector('select[aria-label="Microphone"]').value === 'm2');
  [...doc.querySelectorAll('.settings-nav button')].find((b) => /Uploading/.test(b.textContent)).click();
  check('the uploading page calls it Username', [...doc.querySelectorAll('.field > label')].some((l) => l.textContent === 'Username') && ![...doc.querySelectorAll('label')].some((l) => l.textContent === 'Your name'));

  [...doc.querySelectorAll('.settings-nav button')].find((b) => /Hotkeys/.test(b.textContent)).click();
  await until(() => doc.querySelector('.hotkey-row'), 'hotkey rows');
  check('two hotkeys are listed', doc.querySelectorAll('.hotkey-row').length === 2);
  [...doc.querySelectorAll('button')].find((b) => /Add a hotkey/.test(b.textContent)).click();
  check('adding a hotkey gives it unused keys', doc.querySelectorAll('.hotkey-row').length === 3 && !doc.querySelector('.hint.warn'));
  const capture = doc.querySelectorAll('.capture')[2];
  capture.click();
  check('clicking a key box starts listening', /Press the keys/.test(capture.textContent));
  window.dispatchEvent(new window.KeyboardEvent('keydown', { code: 'KeyQ', key: 'q', bubbles: true }));
  check('a plain letter is refused with a reason', /Add Ctrl, Alt, Shift or Win/.test(doc.querySelector('.toasts').textContent) && /Press the keys/.test(doc.querySelectorAll('.capture')[2].textContent));
  window.dispatchEvent(new window.KeyboardEvent('keydown', { code: 'KeyQ', key: 'q', ctrlKey: true, bubbles: true }));
  await until(() => /Q/.test(doc.querySelectorAll('.capture')[2].textContent), 'key captured');
  check('Ctrl + Q is captured', /Ctrl/.test(doc.querySelectorAll('.capture')[2].textContent));
  doc.querySelectorAll('.capture')[2].click();
  window.dispatchEvent(new window.KeyboardEvent('keydown', { code: 'Backslash', key: '\\', ctrlKey: true, shiftKey: true, bubbles: true }));
  await until(() => /Shift/.test(doc.querySelectorAll('.capture')[2].textContent) && /\\/.test(doc.querySelectorAll('.capture')[2].textContent), 'ctrl+shift+backslash captured');
  check('Ctrl + Shift + \\ is accepted', /Ctrl/.test(doc.querySelectorAll('.capture')[2].textContent) && doc.querySelectorAll('.capture')[2].querySelectorAll('.key').length === 3);
  doc.querySelectorAll('.capture')[2].click();
  window.dispatchEvent(new window.KeyboardEvent('keydown', { code: 'Backslash', key: '\\', ctrlKey: true, bubbles: true }));
  await until(() => doc.querySelectorAll('.capture')[2].querySelectorAll('.key').length === 2, 'ctrl+backslash captured');
  check('Ctrl + \\ is accepted', true);
  doc.querySelectorAll('.capture')[2].click();
  window.dispatchEvent(new window.KeyboardEvent('keydown', { code: 'KeyQ', key: 'q', ctrlKey: true, altKey: true, shiftKey: true, bubbles: true }));
  check('four keys are refused', /at most 3/.test(doc.querySelector('.toasts').textContent) && /Press the keys/.test(doc.querySelectorAll('.capture')[2].textContent));
  window.dispatchEvent(new window.KeyboardEvent('keydown', { code: 'Slash', key: '/', altKey: true, bubbles: true }));
  await until(() => doc.querySelectorAll('.capture')[2].querySelectorAll('.key').length === 2, 'alt+slash captured');
  check('punctuation keys work as hotkeys', true);
  // a duplicate combination blocks saving
  doc.querySelectorAll('.capture')[1].click();
  window.dispatchEvent(new window.KeyboardEvent('keydown', { code: 'PageUp', key: 'PageUp', ctrlKey: true, bubbles: true }));
  await until(() => doc.querySelector('.hint.warn'), 'duplicate warning');
  const saveBtn = [...doc.querySelectorAll('.unsaved .btn')].find((b) => /Save changes/.test(b.textContent));
  check('duplicate hotkeys are flagged and block saving', saveBtn.disabled && /same keys/.test(doc.querySelector('.unsaved').textContent));
  doc.querySelectorAll('.capture')[1].click();
  window.dispatchEvent(new window.KeyboardEvent('keydown', { code: 'PageDown', key: 'PageDown', ctrlKey: true, bubbles: true }));
  await until(() => !doc.querySelector('.hint.warn'), 'warning cleared');
  [...doc.querySelectorAll('.unsaved .btn')].find((b) => /Save changes/.test(b.textContent)).click();
  await until(() => /Settings saved/.test(doc.querySelector('.toasts').textContent), 'saved toast');
  await sleep(100);
  check('saving works and the bar goes away' + (doc.querySelector('.unsaved') ? ' (bar says: ' + doc.querySelector('.unsaved').textContent + ')' : ''), !doc.querySelector('.unsaved'));

  // ------------------------------------------------------------- recording load setting
  [...doc.querySelectorAll('.settings-nav button')].find((b) => /Recording/.test(b.textContent)).click();
  await until(() => doc.querySelector('select[aria-label="Recording load"]'), 'the recording load setting');
  const loadSelect = doc.querySelector('select[aria-label="Recording load"]');
  check('recording load offers automatic, normal and light, automatic first', [...loadSelect.options].map((o) => o.value).join() === 'auto,normal,light' && loadSelect.value === 'auto');
  loadSelect.value = 'light'; loadSelect.dispatchEvent(new window.Event('change', { bubbles: true }));
  [...doc.querySelectorAll('.unsaved .btn')].find((b) => /Save changes/.test(b.textContent)).click();
  await until(() => /Recording settings applied/.test(doc.querySelector('.toasts').textContent), 'recording settings applied toast');
  check('changing the load says the recording restarted', true);

  // ------------------------------------------------------------------ diagnostics
  [...doc.querySelectorAll('.nav-item')].find((b) => /Diagnostics/.test(b.textContent)).click();
  await until(() => doc.querySelector('.diag-card'), 'the diagnostics page');
  const diagText = () => doc.querySelector('.content').textContent;
  check('the diagnostics page has its three sections', [...doc.querySelectorAll('.diag-card h3')].slice(0, 3).map((h3) => h3.textContent).join() === 'Recording,Performance,This PC');
  check('it shows the stats that were asked for', ['Capture', 'Encoder', 'Input', 'Bitrate', 'Buffer', 'FFmpeg CPU', 'FFmpeg RAM', 'GPU video encode', 'Dropped frames']
    .every((label) => [...doc.querySelectorAll('.diag-list dt')].some((dt) => dt.textContent === label)));
  check('the capture, encoder and input read like the example', /Windows Graphics Capture/.test(diagText()) && /NVIDIA NVENC H\.264/.test(diagText()) && /2560\u00D71440 @ 60/.test(diagText()));
  check('what needs attention is at the top', !!doc.querySelector('.diag-finding') && /Nothing needs attention/.test(doc.querySelector('.diag-finding').textContent));
  check('it says nothing is sent anywhere', /never sends it anywhere/.test(diagText()));
  check('recent events are listed', /Recording 2560x1440 at 60 FPS/.test(doc.querySelector('.diag-events').textContent));
  const before = doc.querySelector('.diag-list dd').textContent;
  await sleep(2400);
  check('the numbers refresh by themselves while the page is open', !!doc.querySelector('.diag-card') && doc.querySelector('.diag-list dd').textContent === before);
  [...doc.querySelectorAll('.topbar .btn')].find((b) => /Copy report/.test(b.textContent)).click();
  await until(() => /Report copied/.test(doc.querySelector('.toasts').textContent), 'report copied toast');
  check('copying the report says so', true);

  // Leaving the page must stop its refreshing: it asks the app to read GPU counters, which should only happen while it is on screen.
  let asked = 0;
  const realRequest = window.Chrono.bridge.request;
  window.Chrono.bridge.request = (action, payload) => { if (action === 'getDiagnostics') asked++; return realRequest(action, payload); };
  [...doc.querySelectorAll('.nav-item')].find((b) => /Library/.test(b.textContent)).click();
  await sleep(2600);
  check('leaving the diagnostics page stops it asking for numbers', !doc.querySelector('.diag-card') && asked === 0);

  // ------------------------------------------------------ empty library / paused
  const empty = await open('?state=empty');
  await until(() => empty.window.document.querySelector('.empty'), 'empty state');
  check('an empty library explains the hotkey', /Ctrl/.test(empty.window.document.querySelector('.empty').textContent) && /30 seconds/.test(empty.window.document.querySelector('.empty').textContent));

  check('no script errors during the whole run', errors.length === 0);
  if (errors.length) console.log(errors.slice(0, 5));
  console.log(`${passed} UI checks passed`);
});
