// Tests for the pure helpers in UI/js/util.js (trim maths, formatting, grouping, hotkeys).
// Run:  node --test client/ui-tests/util.test.js
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');

const C = require(path.join(__dirname, '..', 'ChronoRecorder', 'UI', 'js', 'util.js'));

// ------------------------------------------------------------------ formatting

test('formatDuration', () => {
  assert.equal(C.formatDuration(32.47), '0:32');
  assert.equal(C.formatDuration(65), '1:05');
  assert.equal(C.formatDuration(3723), '1:02:03');
  assert.equal(C.formatDuration(0), '0:00');
  assert.equal(C.formatDuration(null), '');
  assert.equal(C.formatDuration(NaN), '');
});

test('formatPrecise shows tenths and never a stray 60', () => {
  assert.equal(C.formatPrecise(7.34), '0:07.3');
  assert.equal(C.formatPrecise(0), '0:00.0');
  assert.equal(C.formatPrecise(65.04), '1:05.0');
  assert.equal(C.formatPrecise(59.96), '1:00.0');
  assert.equal(C.formatPrecise(12.6), '0:12.6');
  assert.equal(C.formatPrecise(-3), '0:00.0');
});

test('formatSize', () => {
  assert.equal(C.formatSize(0), '');
  assert.equal(C.formatSize(500), '1 KB');
  assert.equal(C.formatSize(34_500_000), '33 MB');
  assert.equal(C.formatSize(5_000_000), '4.8 MB');
  assert.equal(C.formatSize(2_000_000_000), '1.9 GB');
});

test('timeAgo', () => {
  const now = new Date(2026, 8, 20, 21, 30, 0);
  const ago = (ms) => new Date(now.getTime() - ms).toISOString();
  assert.equal(C.timeAgo(ago(10_000), now), 'Just now');
  assert.equal(C.timeAgo(ago(60_000), now), '1 minute ago');
  assert.equal(C.timeAgo(ago(5 * 60_000), now), '5 minutes ago');
  assert.equal(C.timeAgo(new Date(2026, 8, 20, 9, 14).toISOString(), now), 'Today at 9:14 AM');
  assert.equal(C.timeAgo(new Date(2026, 8, 19, 23, 5).toISOString(), now), 'Yesterday at 11:05 PM');
  assert.equal(C.timeAgo(new Date(2026, 8, 17, 12, 0).toISOString(), now), '3 days ago');
  assert.equal(C.timeAgo(new Date(2026, 8, 5, 12, 0).toISOString(), now), 'Sep 5');
  assert.equal(C.timeAgo(new Date(2025, 11, 25, 12, 0).toISOString(), now), 'Dec 25, 2025');
  assert.equal(C.timeAgo('nonsense', now), '');
});

test('dateTime', () => {
  assert.equal(C.dateTime(new Date(2026, 8, 20, 21, 14).toISOString()), 'Sep 20, 9:14 PM');
  assert.equal(C.dateTime(new Date(2026, 8, 20, 0, 5).toISOString()), 'Sep 20, 12:05 AM');
  assert.equal(C.dateTime('nope'), '');
});

test('plainDuration', () => {
  assert.equal(C.plainDuration(45), '45 seconds');
  assert.equal(C.plainDuration(60), '1 minute');
  assert.equal(C.plainDuration(140), '2 minutes 20 seconds');
  assert.equal(C.plainDuration(1), '1 second');
});

// ---------------------------------------------------------------------- library

const clip = (id, title, createdUtc, extra = {}) => ({ id, title, game: null, link: null, createdUtc, ...extra });

test('groupClips splits uploaded from not uploaded, newest first', () => {
  const clips = [
    clip('a', 'A', '2026-09-20T10:00:00Z'),
    clip('b', 'B', '2026-09-20T12:00:00Z', { link: 'https://x/watch/b' }),
    clip('c', 'C', '2026-09-20T14:00:00Z'),
    clip('d', 'D', '2026-09-19T14:00:00Z', { link: 'https://x/watch/d' }),
  ];
  const { local, uploaded } = C.groupClips(clips, '');
  assert.deepEqual(local.map((c) => c.id), ['c', 'a']);
  assert.deepEqual(uploaded.map((c) => c.id), ['b', 'd']);
});

test('search matches title or game, ignoring case', () => {
  const clips = [
    clip('a', 'Triple kill', '2026-09-20T10:00:00Z', { game: 'Risk of rain 2' }),
    clip('b', 'Boring', '2026-09-20T11:00:00Z', { game: 'VALORANT' }),
  ];
  assert.deepEqual(C.groupClips(clips, 'TRIPLE').local.map((c) => c.id), ['a']);
  assert.deepEqual(C.groupClips(clips, 'valorant').local.map((c) => c.id), ['b']);
  assert.equal(C.groupClips(clips, 'zzz').local.length, 0);
  assert.equal(C.groupClips(clips, '  ').local.length, 2);
});

test('cleanTitle tidies input and falls back when emptied', () => {
  assert.equal(C.cleanTitle('  Triple   kill \n on the boss ', 'old'), 'Triple kill on the boss');
  assert.equal(C.cleanTitle('', 'old'), 'old');
  assert.equal(C.cleanTitle('   ', 'old'), 'old');
  assert.equal(C.cleanTitle('x'.repeat(150), 'old').length, 100);
});

// ------------------------------------------------------------------- trim maths

test('the start handle cannot pass the end or go below zero', () => {
  assert.equal(C.moveStart(-5, 20), 0);
  assert.equal(C.moveStart(10, 20), 10);
  assert.equal(C.moveStart(25, 20), 19.5);          // stops the minimum length before the end
  assert.equal(C.moveStart(0.2, 0.3), 0);           // a tiny clip can't go negative
});

test('the end handle cannot pass the clip or come too close to the start', () => {
  assert.equal(C.moveEnd(99, 5, 32), 32);
  assert.equal(C.moveEnd(20, 5, 32), 20);
  assert.equal(C.moveEnd(5.1, 5, 32), 5.5);         // keeps the minimum length
  assert.equal(C.moveEnd(1, 31.9, 32), 32);         // near the end: the most it can be
});

test('a drag position maps to a time and back', () => {
  assert.equal(C.timeAtX(100, 100, 400, 40), 0);
  assert.equal(C.timeAtX(300, 100, 400, 40), 20);
  assert.equal(C.timeAtX(500, 100, 400, 40), 40);
  assert.equal(C.timeAtX(9999, 100, 400, 40), 40);  // pointer beyond the timeline
  assert.equal(C.timeAtX(0, 100, 400, 40), 0);
  assert.equal(C.timeAtX(50, 100, 0, 40), 0);       // no layout yet
  assert.equal(C.percentAt(10, 40), 25);
  assert.equal(C.percentAt(50, 40), 100);
  assert.equal(C.percentAt(5, 0), 0);
});

test('a trim only counts once a handle has really moved', () => {
  assert.equal(C.isTrimmed(0, 32.4, 32.4), false);
  assert.equal(C.isTrimmed(0.02, 32.4, 32.4), false);   // jitter
  assert.equal(C.isTrimmed(1, 32.4, 32.4), true);
  assert.equal(C.isTrimmed(0, 30, 32.4), true);
});

test('dragging both handles in sequence always leaves a valid range', () => {
  const duration = 60;
  let start = 0, end = duration;
  // A jumpy sequence of pointer positions, including nonsense ones.
  for (const t of [70, -3, 12, 58, 0.1, 61, 33, 33.2, 0, 59.9, 30]) {
    start = C.moveStart(t, end);
    end = C.moveEnd(t + 5, start, duration);
    assert.ok(start >= 0 && end <= duration, `range ${start}..${end} left the clip`);
    assert.ok(end - start >= C.MIN_TRIM - 1e-9, `range ${start}..${end} is shorter than the minimum`);
  }
});

// ---------------------------------------------------------------------- hotkeys

test('hotkeyName only accepts keys Chrono can register', () => {
  assert.equal(C.hotkeyName('KeyA'), 'A');
  assert.equal(C.hotkeyName('Digit5'), '5');
  assert.equal(C.hotkeyName('F9'), 'F9');
  assert.equal(C.hotkeyName('F24'), 'F24');
  assert.equal(C.hotkeyName('PageUp'), 'PageUp');
  assert.equal(C.hotkeyName('Space'), 'Space');
  assert.equal(C.hotkeyName('F25'), null);
  assert.equal(C.hotkeyName('F0'), null);
  assert.equal(C.hotkeyName('ArrowLeft'), null);
  assert.equal(C.hotkeyName('Backquote'), null);
  assert.equal(C.hotkeyName('Numpad1'), null);
});

test('modifiersOf lists them in the order Chrono saves them', () => {
  assert.deepEqual(C.modifiersOf({ ctrlKey: true, altKey: false, shiftKey: true, metaKey: false }), ['Control', 'Shift']);
  assert.deepEqual(C.modifiersOf({}), []);
});

test('plain letters need a modifier but F-keys and PageUp do not', () => {
  assert.ok(C.hotkeyProblem([], 'A'));
  assert.ok(C.hotkeyProblem([], '5'));
  assert.ok(C.hotkeyProblem([], 'Space'));
  assert.equal(C.hotkeyProblem(['Control'], 'A'), null);
  assert.equal(C.hotkeyProblem([], 'F9'), null);
  assert.equal(C.hotkeyProblem([], 'PageUp'), null);
});

test('buffer length is the longest hotkey plus two segments', () => {
  assert.equal(C.bufferSeconds([{ ClipLengthSeconds: 30 }, { ClipLengthSeconds: 120 }]), 140);
  assert.equal(C.bufferSeconds([]), 20);
});
