import { describe, expect, it } from "vitest";
import { isAuthorized } from "../src/auth";
import { escapeHtml, formatDuration } from "../src/html";
import { isClipId, newClipId } from "../src/ids";
import { parseRange } from "../src/range";
import { MAX_TITLE_LENGTH, parseNewClip, parseTitle, sanitizeFilename } from "../src/validate";
import { clipTitle } from "../src/html";

function req(authorization?: string): Request {
  return new Request("https://x.test/", { headers: authorization ? { Authorization: authorization } : {} });
}

describe("isAuthorized", () => {
  it("accepts the right bearer key", async () => {
    expect(await isAuthorized(req("Bearer secret"), "secret")).toBe(true);
  });

  it.each([
    ["no header", undefined],
    ["wrong key", "Bearer other"],
    ["prefix of the key", "Bearer secre"],
    ["longer than the key", "Bearer secretsecret"],
    ["wrong scheme", "Basic secret"],
    ["no scheme", "secret"],
    ["empty token", "Bearer "],
  ])("rejects %s", async (_name, header) => {
    expect(await isAuthorized(req(header), "secret")).toBe(false);
  });

  it("fails closed when no key is configured", async () => {
    expect(await isAuthorized(req("Bearer "), "")).toBe(false);
    expect(await isAuthorized(req("Bearer anything"), undefined)).toBe(false);
  });
});

describe("escapeHtml", () => {
  it("escapes everything that can break out of markup or attributes", () => {
    expect(escapeHtml(`<script>"a" & 'b'</script>`)).toBe("&lt;script&gt;&quot;a&quot; &amp; &#39;b&#39;&lt;/script&gt;");
  });
});

describe("formatDuration", () => {
  it.each([
    [0, "0:00"],
    [9.4, "0:09"],
    [65, "1:05"],
    [3725, "1:02:05"],
  ])("formats %d seconds as %s", (seconds, expected) => {
    expect(formatDuration(seconds)).toBe(expected);
  });
});

describe("clip ids", () => {
  it("are 12 URL-safe characters and recognised as valid", () => {
    const id = newClipId();
    expect(id).toMatch(/^[A-Za-z0-9_-]{12}$/);
    expect(isClipId(id)).toBe(true);
  });

  it.each(["", "short", "a".repeat(13), "../../etc/pa", "has space!!!"])("rejects %j", (bad) => {
    expect(isClipId(bad)).toBe(false);
  });
});

describe("parseRange", () => {
  const size = 1000;

  it("returns null when there is no range", () => {
    expect(parseRange(null, size)).toBeNull();
    expect(parseRange("", size)).toBeNull();
  });

  it.each([
    ["bytes=0-99", { offset: 0, length: 100 }],
    ["bytes=900-", { offset: 900, length: 100 }],
    ["bytes=-100", { offset: 900, length: 100 }],
    ["bytes=990-5000", { offset: 990, length: 10 }],   // end past the file is clamped
    ["bytes=-5000", { offset: 0, length: 1000 }],      // suffix longer than the file is the whole file
    ["bytes=0-0", { offset: 0, length: 1 }],
  ])("parses %s", (header, expected) => {
    expect(parseRange(header, size)).toEqual(expected);
  });

  it.each(["bytes=1000-", "bytes=1500-2000", "bytes=-0"])("marks %s unsatisfiable", (header) => {
    expect(parseRange(header, size)).toBe("unsatisfiable");
  });

  it.each(["bytes=0-1,5-9", "items=0-5", "bytes=abc", "bytes=-", "bytes=50-10"])(
    "ignores %s and serves the whole file",
    (header) => {
      expect(parseRange(header, size)).toBeNull();
    },
  );
});

describe("sanitizeFilename", () => {
  it.each([
    ["Quick Clip_2026-09-20_16-48-36.mp4", "Quick Clip_2026-09-20_16-48-36.mp4"],
    ["<script>alert(1)</script>.mp4", "_script_alert_1___script_.mp4"],
    ["../../etc/passwd.mp4", ".._.._etc_passwd.mp4"],
    [".mp4", "clip.mp4"],
    ["clip.MP4", "clip.mp4"],
  ])("cleans %j", (input, expected) => {
    expect(sanitizeFilename(input)).toBe(expected);
  });

  it("truncates very long names but keeps the extension", () => {
    const result = sanitizeFilename("a".repeat(500) + ".mp4");
    expect(result.length).toBeLessThanOrEqual(104);
    expect(result.endsWith(".mp4")).toBe(true);
  });
});

describe("parseNewClip", () => {
  it("accepts a complete request", () => {
    const parsed = parseNewClip({ username: "olly", filename: "a.mp4", size: 5, duration: 1.5, resolution: "1920x1080", fps: 60, bitrate: 8000 }, 100);
    expect(parsed).toEqual({
      ok: true,
      value: { owner: "olly", filename: "a.mp4", title: null, size: 5, duration: 1.5, resolution: "1920x1080", fps: 60, bitrate: 8000 },
    });
  });

  it("treats the metadata as optional", () => {
    const parsed = parseNewClip({ username: "olly", size: 5 }, 100);
    expect(parsed).toMatchObject({ ok: true, value: { filename: "clip.mp4", duration: null, resolution: null, fps: null, bitrate: null } });
  });

  it("uses 413 for oversized clips and 400 for everything else", () => {
    expect(parseNewClip({ username: "olly", size: 101 }, 100)).toMatchObject({ ok: false, status: 413 });
    expect(parseNewClip({ username: "olly", size: -1 }, 100)).toMatchObject({ ok: false, status: 400 });
    expect(parseNewClip("nope", 100)).toMatchObject({ ok: false, status: 400 });
  });
});

describe("parseTitle", () => {
  it("keeps what a person typed, tidied up", () => {
    expect(parseTitle("  Triple kill   on the boss ")).toEqual({ ok: true, value: "Triple kill on the boss" });
  });

  it("turns control characters and line breaks into spaces", () => {
    expect(parseTitle("a\nb\u0000c\td")).toEqual({ ok: true, value: "a b c d" });
  });

  it("treats missing or blank titles as no title", () => {
    expect(parseTitle(undefined)).toEqual({ ok: true, value: null });
    expect(parseTitle(null)).toEqual({ ok: true, value: null });
    expect(parseTitle("   ")).toEqual({ ok: true, value: null });
  });

  it("rejects titles that are too long or not text", () => {
    expect(parseTitle("x".repeat(MAX_TITLE_LENGTH))).toMatchObject({ ok: true });
    expect(parseTitle("x".repeat(MAX_TITLE_LENGTH + 1))).toMatchObject({ ok: false });
    expect(parseTitle(42)).toMatchObject({ ok: false });
  });

  it("is used by parseNewClip", () => {
    expect(parseNewClip({ username: "olly", size: 5, title: "Nice one" }, 100)).toMatchObject({ ok: true, value: { title: "Nice one" } });
    expect(parseNewClip({ username: "olly", size: 5, title: 7 }, 100)).toMatchObject({ ok: false, status: 400 });
  });
});

describe("clipTitle", () => {
  it("prefers the owner's title and falls back to the old wording", () => {
    expect(clipTitle({ title: "Nice one", owner: "olly" })).toBe("Nice one");
    expect(clipTitle({ title: null, owner: "olly" })).toBe("olly's clip");
  });
});
