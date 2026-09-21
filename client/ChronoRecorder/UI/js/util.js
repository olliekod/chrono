// Small helpers shared by every page. Plain scripts sharing one global, `Chrono`, so the pure functions below can
// be tested in Node without a browser.
(function (root) {
  const Chrono = root.Chrono || (root.Chrono = {});

  /** Build an element. Text is always set as text, never as HTML, so a clip title can't inject markup. */
  function h(tag, props, ...children) {
    const el = root.document.createElement(tag);
    for (const [key, value] of Object.entries(props || {})) {
      if (value === undefined || value === null || value === false) continue;
      if (key === 'class') el.className = value;
      else if (key === 'text') el.textContent = value;
      else if (key.startsWith('on') && typeof value === 'function') el.addEventListener(key.slice(2).toLowerCase(), value);
      else if (key === 'dataset') Object.assign(el.dataset, value);
      else if (key === 'style' && typeof value === 'object') Object.assign(el.style, value);
      else el.setAttribute(key, value === true ? '' : value);
    }
    for (const child of children.flat()) {
      if (child === undefined || child === null || child === false) continue;
      el.append(child.nodeType ? child : root.document.createTextNode(String(child)));
    }
    return el;
  }

  const pad = (n) => String(n).padStart(2, '0');

  /** 32.47 -> "0:32", 65 -> "1:05", 3723 -> "1:02:03". */
  function formatDuration(seconds) {
    if (seconds === null || seconds === undefined || !isFinite(seconds)) return '';
    const total = Math.max(0, Math.round(seconds));
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const secs = total % 60;
    return hours > 0 ? `${hours}:${pad(minutes)}:${pad(secs)}` : `${minutes}:${pad(secs)}`;
  }

  /** With a tenth of a second, for the trim handles: 7.34 -> "0:07.3". */
  function formatPrecise(seconds) {
    if (!isFinite(seconds)) return '0:00.0';
    const tenths = Math.max(0, Math.round(seconds * 10));
    const minutes = Math.floor(tenths / 600);
    const rest = (tenths % 600) / 10;
    return `${minutes}:${rest < 10 ? '0' : ''}${rest.toFixed(1)}`;
  }

  function formatSize(bytes) {
    if (!bytes || bytes < 0) return '';
    if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
    if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1048576).toFixed(bytes < 10 * 1048576 ? 1 : 0)} MB`;
    return `${(bytes / 1073741824).toFixed(1)} GB`;
  }

  const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

  /** "Just now", "5 minutes ago", "Today at 9:14 PM", "Yesterday", "Sep 12". */
  function timeAgo(iso, now) {
    const then = new Date(iso);
    const current = now ? new Date(now) : new Date();
    const seconds = (current - then) / 1000;
    if (isNaN(seconds)) return '';
    if (seconds < 45) return 'Just now';
    if (seconds < 3600) { const m = Math.max(1, Math.round(seconds / 60)); return `${m} minute${m === 1 ? '' : 's'} ago`; }

    const startOf = (d) => new Date(d.getFullYear(), d.getMonth(), d.getDate());
    const days = Math.round((startOf(current) - startOf(then)) / 86400000);
    const hour = then.getHours();
    const clock = `${hour % 12 === 0 ? 12 : hour % 12}:${pad(then.getMinutes())} ${hour < 12 ? 'AM' : 'PM'}`;
    if (days === 0) return `Today at ${clock}`;
    if (days === 1) return `Yesterday at ${clock}`;
    if (days < 7) return `${days} days ago`;
    const sameYear = then.getFullYear() === current.getFullYear();
    return `${MONTHS[then.getMonth()]} ${then.getDate()}${sameYear ? '' : ', ' + then.getFullYear()}`;
  }

  /** "Sep 20, 9:14 PM" */
  function dateTime(iso) {
    const d = new Date(iso);
    if (isNaN(d)) return '';
    const hour = d.getHours();
    return `${MONTHS[d.getMonth()]} ${d.getDate()}, ${hour % 12 === 0 ? 12 : hour % 12}:${pad(d.getMinutes())} ${hour < 12 ? 'AM' : 'PM'}`;
  }

  /** Case-insensitive match on title or game; an empty query matches everything. */
  function matches(clip, query) {
    const q = (query || '').trim().toLowerCase();
    if (!q) return true;
    return (clip.title || '').toLowerCase().includes(q) || (clip.game || '').toLowerCase().includes(q);
  }

  /** Games that appear in the library, alphabetically, for the Game filter. Clips with no known game are not listed. */
  function gameOptions(clips) {
    return [...new Set(clips.map((c) => c.game).filter(Boolean))].sort((a, b) => a.localeCompare(b));
  }

  /** Is the clip within the chosen time range? 'any' | 'today' | 'week' (last 7 days) | 'month' (last 30 days). */
  function inRange(iso, range, now) {
    if (!range || range === 'any') return true;
    const then = new Date(iso);
    const current = now ? new Date(now) : new Date();
    if (isNaN(then)) return false;
    if (range === 'today') return then.toDateString() === current.toDateString();
    const days = { week: 7, month: 30 }[range];
    return days ? current - then <= days * 86400000 && then <= current : true;
  }

  /**
   * The clips that pass every filter. filters: { query, game ('' = any), when ('any'|'today'|'week'|'month'),
   * where ('all'|'uploaded'|'local') }. Newest first.
   */
  function filterClips(clips, filters, now) {
    const f = filters || {};
    return clips
      .filter((c) => matches(c, f.query))
      .filter((c) => !f.game || c.game === f.game)
      .filter((c) => inRange(c.createdUtc, f.when, now))
      .filter((c) => (f.where === 'uploaded' ? !!c.link : f.where === 'local' ? !c.link : true))
      .sort((a, b) => new Date(b.createdUtc) - new Date(a.createdUtc));
  }

  /** True when any filter is narrowing the list. */
  function isFiltering(filters) {
    const f = filters || {};
    return !!((f.query || '').trim() || f.game || (f.when && f.when !== 'any') || (f.where && f.where !== 'all'));
  }

  /** Split for the library: uploaded (shown first, they matter more) and still only on this PC. filters may be a search string. */
  function groupClips(clips, filters, now) {
    const shown = filterClips(clips, typeof filters === 'string' ? { query: filters } : filters, now);
    return { uploaded: shown.filter((c) => !!c.link), local: shown.filter((c) => !c.link) };
  }

  // ---- trim maths (kept free of the DOM so it can be tested) ----

  const MIN_TRIM = 0.5;

  const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

  /** Move the start handle: never past the end minus the shortest clip, never before 0. */
  function moveStart(time, end, minLength = MIN_TRIM) {
    return clamp(time, 0, Math.max(0, end - minLength));
  }

  /** Move the end handle: never before the start plus the shortest clip, never past the length. */
  function moveEnd(time, start, duration, minLength = MIN_TRIM) {
    return clamp(time, Math.min(duration, start + minLength), duration);
  }

  /** Where a pointer is on the timeline, as a time in the clip. */
  function timeAtX(x, left, width, duration) {
    if (width <= 0) return 0;
    return clamp((x - left) / width, 0, 1) * duration;
  }

  const percentAt = (time, duration) => (duration > 0 ? clamp(time / duration, 0, 1) * 100 : 0);

  /** True when the handles have moved off the ends by enough to be a real trim. */
  function isTrimmed(start, end, duration, tolerance = 0.05) {
    return start > tolerance || end < duration - tolerance;
  }

  /** The clip title's fallback when the box is emptied: keep the last saved one. */
  function cleanTitle(text, fallback) {
    const cleaned = String(text || '').replace(/[\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 100).trim();
    return cleaned || fallback;
  }

  // ---- hotkeys ----

  /** Keys with a name of their own. The names are the browser's key codes, which Chrono's hotkey parser understands. */
  const NAMED_KEYS = new Set([
    'PageUp', 'PageDown', 'Home', 'End', 'Insert', 'Delete', 'Space', 'Enter', 'Tab', 'Backspace', 'Escape',
    'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'PrintScreen', 'ScrollLock', 'Pause',
    'Backslash', 'Slash', 'Comma', 'Period', 'Semicolon', 'Quote', 'BracketLeft', 'BracketRight', 'Minus', 'Equal', 'Backquote', 'IntlBackslash',
    'NumpadMultiply', 'NumpadAdd', 'NumpadSubtract', 'NumpadDecimal', 'NumpadDivide',
  ]);

  /** The name Chrono's hotkey parser accepts for a pressed key (its browser key code), or null when it can't be used. */
  function hotkeyName(code) {
    if (/^Key[A-Z]$/.test(code)) return code.slice(3);
    if (/^Digit[0-9]$/.test(code)) return code.slice(5);
    if (/^F([1-9]|1[0-9]|2[0-4])$/.test(code)) return code;
    if (/^Numpad[0-9]$/.test(code)) return code;
    return NAMED_KEYS.has(code) ? code : null;
  }

  function modifiersOf(event) {
    const mods = [];
    if (event.ctrlKey) mods.push('Control');
    if (event.altKey) mods.push('Alt');
    if (event.shiftKey) mods.push('Shift');
    if (event.metaKey) mods.push('Win');
    return mods;
  }

  const MAX_KEYS = 3;

  /** Keys that are safe on their own. Everything else is typed or pressed constantly, so it needs a modifier. */
  function worksAlone(key) {
    return /^F([1-9]|1[0-9]|2[0-4])$/.test(key) || ['PageUp', 'PageDown', 'Home', 'End', 'Insert', 'Delete', 'PrintScreen', 'ScrollLock', 'Pause'].includes(key);
  }

  /** Why a key combination can't be a hotkey, or null when it can: at most 3 keys, and typed keys need Ctrl, Alt, Shift or Win. */
  function hotkeyProblem(mods, key) {
    const count = new Set(mods || []).size + 1;
    if (count > MAX_KEYS) return `Use at most ${MAX_KEYS} keys, for example Ctrl + Shift + a key.`;
    if ((!mods || mods.length === 0) && !worksAlone(key)) return 'Add Ctrl, Alt, Shift or Win. On its own this key would trigger whenever you type or navigate.';
    return null;
  }

  const KEY_LABELS = {
    Control: 'Ctrl', Backslash: '\\', Slash: '/', Comma: ',', Period: '.', Semicolon: ';', Quote: "'", BracketLeft: '[', BracketRight: ']',
    Minus: '-', Equal: '=', Backquote: '`', IntlBackslash: '\\', ArrowUp: '↑', ArrowDown: '↓', ArrowLeft: '←', ArrowRight: '→',
    NumpadMultiply: 'Num *', NumpadAdd: 'Num +', NumpadSubtract: 'Num -', NumpadDecimal: 'Num .', NumpadDivide: 'Num /',
    PageUp: 'PgUp', PageDown: 'PgDn', Escape: 'Esc', Backspace: 'Backspace', PrintScreen: 'PrtSc', ScrollLock: 'ScrLk',
  };

  /** How a key is written on a key cap: "Ctrl", "\", "Num 5". */
  function hotkeyLabel(part) {
    if (KEY_LABELS[part]) return KEY_LABELS[part];
    const numpad = /^Numpad([0-9])$/.exec(part);
    return numpad ? `Num ${numpad[1]}` : part;
  }

  // ---- microphone level meter ----

  const METER_FLOOR_DB = 60;

  /**
   * Where a microphone's peak lands on the level bar (0-100), after the chosen volume. Bars work in decibels because
   * that is how loudness is heard: 0 = silence (-60 dB or less), 100 = full scale. The bar's green/yellow/red zones
   * are at 70 (-18 dB) and 90 (-6 dB).
   */
  function levelPercent(peak, volumePercent) {
    const level = (peak || 0) * ((volumePercent === undefined ? 100 : volumePercent) / 100);
    if (level <= 0) return 0;
    const db = 20 * Math.log10(level);
    return clamp(((db + METER_FLOOR_DB) / METER_FLOOR_DB) * 100, 0, 100);
  }

  /** True when the boosted microphone would pass full scale, which the recording then has to squash with a limiter. */
  function isLoud(peak, volumePercent) {
    return (peak || 0) * ((volumePercent === undefined ? 100 : volumePercent) / 100) >= 1;
  }

  const hotkeyParts = (hotkey) => [...(hotkey.Modifiers || []), hotkey.Key].filter(Boolean);

  /** How much footage Chrono keeps, from the longest hotkey: that plus two 10-second segments. */
  function bufferSeconds(hotkeys) {
    const longest = Math.max(0, ...(hotkeys || []).map((k) => k.ClipLengthSeconds || 0));
    return longest + 20;
  }

  /** "2 minutes 20 seconds", "45 seconds". */
  function plainDuration(seconds) {
    const m = Math.floor(seconds / 60);
    const s = Math.round(seconds % 60);
    const parts = [];
    if (m) parts.push(`${m} minute${m === 1 ? '' : 's'}`);
    if (s || !m) parts.push(`${s} second${s === 1 ? '' : 's'}`);
    return parts.join(' ');
  }

  Object.assign(Chrono, {
    h, formatDuration, formatPrecise, formatSize, timeAgo, dateTime, matches, groupClips, gameOptions, filterClips, inRange, isFiltering,
    MIN_TRIM, clamp, moveStart, moveEnd, timeAtX, percentAt, isTrimmed, cleanTitle,
    hotkeyName, modifiersOf, hotkeyProblem, hotkeyLabel, worksAlone, MAX_KEYS, levelPercent, isLoud, hotkeyParts, bufferSeconds, plainDuration,
  });

  if (typeof module !== 'undefined' && module.exports) module.exports = Chrono;
})(typeof window !== 'undefined' ? window : globalThis);
