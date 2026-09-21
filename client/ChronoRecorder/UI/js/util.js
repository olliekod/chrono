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

  /** Split the clips for the library: not uploaded yet, and uploaded (each newest first), after the search. */
  function groupClips(clips, query) {
    const shown = clips.filter((c) => matches(c, query)).sort((a, b) => new Date(b.createdUtc) - new Date(a.createdUtc));
    return { local: shown.filter((c) => !c.link), uploaded: shown.filter((c) => !!c.link) };
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

  const KEY_NAMES = { PageUp: 'PageUp', PageDown: 'PageDown', Home: 'Home', End: 'End', Insert: 'Insert', Delete: 'Delete', Space: 'Space', Enter: 'Enter' };

  /** The name Chrono's hotkey parser accepts for a pressed key, or null when it can't be used. */
  function hotkeyName(code) {
    if (/^Key[A-Z]$/.test(code)) return code.slice(3);
    if (/^Digit[0-9]$/.test(code)) return code.slice(5);
    if (/^F([1-9]|1[0-9]|2[0-4])$/.test(code)) return code;
    return KEY_NAMES[code] || null;
  }

  function modifiersOf(event) {
    const mods = [];
    if (event.ctrlKey) mods.push('Control');
    if (event.altKey) mods.push('Alt');
    if (event.shiftKey) mods.push('Shift');
    if (event.metaKey) mods.push('Win');
    return mods;
  }

  /**
   * Why a key combination can't be a hotkey, or null when it can. Letters, digits, Space and Enter alone would hijack
   * typing everywhere, so they need Ctrl, Alt, Shift or Win; F-keys, PageUp and the like are fine on their own.
   */
  function hotkeyProblem(mods, key) {
    const needsModifier = /^[A-Z0-9]$/.test(key) || key === 'Space' || key === 'Enter';
    if (needsModifier && (!mods || mods.length === 0)) return 'Add Ctrl, Alt or Shift. A plain letter, number, Space or Enter would trigger while you type.';
    return null;
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
    h, formatDuration, formatPrecise, formatSize, timeAgo, dateTime, matches, groupClips,
    MIN_TRIM, clamp, moveStart, moveEnd, timeAtX, percentAt, isTrimmed, cleanTitle,
    hotkeyName, modifiersOf, hotkeyProblem, hotkeyParts, bufferSeconds, plainDuration,
  });

  if (typeof module !== 'undefined' && module.exports) module.exports = Chrono;
})(typeof window !== 'undefined' ? window : globalThis);
