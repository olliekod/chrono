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

const NOW = new Date(2026, 8, 20, 21, 0, 0);
const at = (daysAgo, hour = 12) => new Date(2026, 8, 20 - daysAgo, hour, 0).toISOString();

test('uploaded clips come first', () => {
  const { uploaded, local } = C.groupClips([clip('a', 'A', at(1)), clip('b', 'B', at(2), { link: 'https://x/b' })], '', NOW);
  assert.deepEqual(uploaded.map((c) => c.id), ['b']);
  assert.deepEqual(local.map((c) => c.id), ['a']);
  assert.equal(Object.keys(C.groupClips([], '', NOW))[0], 'uploaded');
});

test('filter by game', () => {
  const clips = [clip('a', 'A', at(1), { game: 'VALORANT' }), clip('b', 'B', at(1), { game: 'Deadlock' }), clip('c', 'C', at(1))];
  assert.deepEqual(C.filterClips(clips, { game: 'VALORANT' }, NOW).map((c) => c.id), ['a']);
  assert.equal(C.filterClips(clips, { game: '' }, NOW).length, 3);
  assert.deepEqual(C.gameOptions(clips), ['Deadlock', 'VALORANT']);
});

test('filter by uploaded or on this PC', () => {
  const clips = [clip('a', 'A', at(1)), clip('b', 'B', at(1), { link: 'https://x/b' })];
  assert.deepEqual(C.filterClips(clips, { where: 'uploaded' }, NOW).map((c) => c.id), ['b']);
  assert.deepEqual(C.filterClips(clips, { where: 'local' }, NOW).map((c) => c.id), ['a']);
  assert.equal(C.filterClips(clips, { where: 'all' }, NOW).length, 2);
});

test('filter by date', () => {
  const clips = [clip('today', 'T', at(0, 9)), clip('three', 'T', at(3)), clip('ten', 'T', at(10)), clip('forty', 'T', at(40))];
  const ids = (when) => C.filterClips(clips, { when }, NOW).map((c) => c.id);
  assert.deepEqual(ids('today'), ['today']);
  assert.deepEqual(ids('week'), ['today', 'three']);
  assert.deepEqual(ids('month'), ['today', 'three', 'ten']);
  assert.equal(ids('any').length, 4);
  assert.equal(C.inRange('nonsense', 'week', NOW), false);
});

test('filters combine, and isFiltering knows when one is on', () => {
  const clips = [
    clip('a', 'Triple kill', at(1), { game: 'VALORANT', link: 'https://x/a' }),
    clip('b', 'Triple kill', at(1), { game: 'VALORANT' }),
    clip('c', 'Triple kill', at(20), { game: 'VALORANT', link: 'https://x/c' }),
  ];
  const f = { query: 'triple', game: 'VALORANT', when: 'week', where: 'uploaded' };
  assert.deepEqual(C.filterClips(clips, f, NOW).map((c) => c.id), ['a']);
  assert.equal(C.isFiltering(f), true);
  assert.equal(C.isFiltering({ query: '  ', game: '', when: 'any', where: 'all' }), false);
  assert.equal(C.isFiltering(undefined), false);
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

test('hotkeyName knows every key Chrono can register', () => {
  assert.equal(C.hotkeyName('KeyA'), 'A');
  assert.equal(C.hotkeyName('Digit5'), '5');
  assert.equal(C.hotkeyName('F9'), 'F9');
  assert.equal(C.hotkeyName('F24'), 'F24');
  assert.equal(C.hotkeyName('PageUp'), 'PageUp');
  assert.equal(C.hotkeyName('Space'), 'Space');
  assert.equal(C.hotkeyName('Backslash'), 'Backslash');      // Ctrl + \ used to be refused
  assert.equal(C.hotkeyName('Slash'), 'Slash');
  assert.equal(C.hotkeyName('Comma'), 'Comma');
  assert.equal(C.hotkeyName('BracketLeft'), 'BracketLeft');
  assert.equal(C.hotkeyName('Backquote'), 'Backquote');
  assert.equal(C.hotkeyName('ArrowLeft'), 'ArrowLeft');
  assert.equal(C.hotkeyName('Tab'), 'Tab');
  assert.equal(C.hotkeyName('Numpad1'), 'Numpad1');
  assert.equal(C.hotkeyName('NumpadAdd'), 'NumpadAdd');
  assert.equal(C.hotkeyName('PrintScreen'), 'PrintScreen');
  assert.equal(C.hotkeyName('F25'), null);
  assert.equal(C.hotkeyName('F0'), null);
  assert.equal(C.hotkeyName('ShiftLeft'), null);
  assert.equal(C.hotkeyName('MediaPlayPause'), null);
});

test('modifiersOf lists them in the order Chrono saves them', () => {
  assert.deepEqual(C.modifiersOf({ ctrlKey: true, altKey: false, shiftKey: true, metaKey: false }), ['Control', 'Shift']);
  assert.deepEqual(C.modifiersOf({}), []);
});

test('up to three keys are allowed, four are not', () => {
  assert.equal(C.hotkeyProblem(['Control'], 'Backslash'), null);
  assert.equal(C.hotkeyProblem(['Control', 'Shift'], 'Backslash'), null);       // Ctrl + Shift + \
  assert.equal(C.hotkeyProblem(['Alt', 'Shift'], 'Q'), null);
  assert.match(C.hotkeyProblem(['Control', 'Alt', 'Shift'], 'A'), /at most 3/);
  assert.match(C.hotkeyProblem(['Control', 'Alt', 'Shift', 'Win'], 'F9'), /at most 3/);
});

test('keys people type need a modifier, F-keys and navigation keys do not', () => {
  for (const key of ['A', '5', 'Space', 'Enter', 'Backslash', 'ArrowLeft', 'Tab', 'Escape', 'Numpad5']) {
    assert.match(C.hotkeyProblem([], key), /Add Ctrl/, key);
  }
  for (const key of ['F1', 'F24', 'PageUp', 'End', 'Insert', 'PrintScreen']) {
    assert.equal(C.hotkeyProblem([], key), null, key);
  }
});

test('hotkey labels read like key caps', () => {
  assert.equal(C.hotkeyLabel('Control'), 'Ctrl');
  assert.equal(C.hotkeyLabel('Backslash'), '\\');
  assert.equal(C.hotkeyLabel('Numpad5'), 'Num 5');
  assert.equal(C.hotkeyLabel('ArrowUp'), '\u2191');
  assert.equal(C.hotkeyLabel('F9'), 'F9');
  assert.equal(C.hotkeyLabel('Q'), 'Q');
});

test('the level bar works in decibels', () => {
  assert.equal(C.levelPercent(0, 100), 0);
  assert.equal(C.levelPercent(1, 100), 100);                                   // full scale
  assert.ok(Math.abs(C.levelPercent(0.125893, 100) - 70) < 0.1);                // -18 dB: the top of the green zone
  assert.ok(Math.abs(C.levelPercent(0.501187, 100) - 90) < 0.1);                // -6 dB: the top of the yellow zone
  assert.equal(C.levelPercent(0.0000001, 100), 0);                             // far below the floor
});

test('the microphone volume moves the bar, and can push it past full scale', () => {
  const quiet = C.levelPercent(0.05, 100);
  assert.ok(C.levelPercent(0.05, 400) > quiet + 20, 'four times louder is well up the bar');
  assert.ok(C.levelPercent(0.05, 50) < quiet);
  assert.equal(C.levelPercent(0.6, 500), 100);                                 // capped at the top
  assert.equal(C.isLoud(0.3, 100), false);
  assert.equal(C.isLoud(0.3, 400), true);                                      // 1.2 x full scale: the limiter will squash it
  assert.equal(C.isLoud(0, 500), false);
});

test('buffer length is the longest hotkey plus two segments', () => {
  assert.equal(C.bufferSeconds([{ ClipLengthSeconds: 30 }, { ClipLengthSeconds: 120 }]), 140);
  assert.equal(C.bufferSeconds([]), 20);
});

// ------------------------------------------------------------ server address

test('serverAddressProblem accepts what the app accepts', () => {
  for (const ok of ['https://clips.example.workers.dev', 'clips.example.workers.dev', '  https://clips.example.workers.dev/  ', 'http://localhost:8799', 'http://127.0.0.1:8799/']) {
    assert.equal(C.serverAddressProblem(ok), '', ok);
  }
});

test('serverAddressProblem explains what is wrong', () => {
  assert.match(C.serverAddressProblem(''), /Enter the address/);
  assert.match(C.serverAddressProblem('   '), /Enter the address/);
  assert.match(C.serverAddressProblem('http://clips.example.com'), /https/);
  assert.match(C.serverAddressProblem('ftp://clips.example.com'), /https/);
  assert.match(C.serverAddressProblem('https://'), /address/);
  assert.match(C.serverAddressProblem('https://chrono-clips.fly.dev'), /doesn't exist/);
});
