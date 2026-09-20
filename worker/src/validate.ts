export const MAX_PARTS = 10_000; // R2 multipart limit

export type Parsed<T> = { ok: true; value: T } | { ok: false; status: 400 | 413; error: string };

export interface NewClip {
  owner: string;
  filename: string;
  size: number;
  duration: number | null;
  resolution: string | null;
  fps: number | null;
  bitrate: number | null;
}

export interface CompletedPart {
  partNumber: number;
  etag: string;
}

const bad = (error: string): { ok: false; status: 400; error: string } => ({ ok: false, status: 400, error });

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Only characters that are harmless in HTML, URLs and file names. Always ends in .mp4. */
export function sanitizeFilename(name: string): string {
  const cleaned = name.replace(/[^A-Za-z0-9 ._-]/g, "_").trim();
  const stem = cleaned.replace(/\.mp4$/i, "").slice(0, 100);
  return `${stem || "clip"}.mp4`;
}

function optionalNumber(value: unknown, min: number, max: number, integer: boolean): number | null | undefined {
  if (value === undefined || value === null) return null;
  if (typeof value !== "number" || !Number.isFinite(value)) return undefined;
  if (value < min || value > max) return undefined;
  if (integer && !Number.isInteger(value)) return undefined;
  return value;
}

export function parseNewClip(input: unknown, maxBytes: number): Parsed<NewClip> {
  if (!isRecord(input)) return bad("Expected a JSON object");

  const { username, filename, size, duration, resolution, fps, bitrate } = input;

  if (typeof username !== "string" || !/^[A-Za-z0-9_-]{1,32}$/.test(username)) {
    return bad("username must be 1-32 letters, digits, _ or -");
  }

  if (typeof size !== "number" || !Number.isSafeInteger(size) || size < 1) {
    return bad("size must be a positive integer (bytes)");
  }
  if (size > maxBytes) {
    return { ok: false, status: 413, error: `Clip is larger than the ${maxBytes} byte limit` };
  }

  if (filename !== undefined && typeof filename !== "string") return bad("filename must be a string");
  const rawName = filename ?? "clip.mp4";
  if (!/\.mp4$/i.test(rawName)) return bad("Only .mp4 clips are supported");

  if (resolution !== undefined && resolution !== null) {
    if (typeof resolution !== "string" || !/^\d{2,5}x\d{2,5}$/.test(resolution)) {
      return bad("resolution must look like 1920x1080");
    }
  }

  const durationValue = optionalNumber(duration, 0.001, 86_400, false);
  if (durationValue === undefined) return bad("duration must be a number of seconds");

  const fpsValue = optionalNumber(fps, 1, 1000, true);
  if (fpsValue === undefined) return bad("fps must be a positive integer");

  const bitrateValue = optionalNumber(bitrate, 1, 1_000_000, true);
  if (bitrateValue === undefined) return bad("bitrate must be a positive integer (kbps)");

  return {
    ok: true,
    value: {
      owner: username,
      filename: sanitizeFilename(rawName),
      size,
      duration: durationValue,
      resolution: (resolution as string | null | undefined) ?? null,
      fps: fpsValue,
      bitrate: bitrateValue,
    },
  };
}

export function parseCompletion(input: unknown): Parsed<{ parts: CompletedPart[] }> {
  if (!isRecord(input) || !Array.isArray(input.parts)) return bad("Expected {\"parts\": [...]}");
  if (input.parts.length > MAX_PARTS) return bad("Too many parts");

  const parts: CompletedPart[] = [];
  for (const part of input.parts) {
    if (
      !isRecord(part) ||
      typeof part.partNumber !== "number" ||
      !Number.isInteger(part.partNumber) ||
      part.partNumber < 1 ||
      part.partNumber > MAX_PARTS ||
      typeof part.etag !== "string" ||
      part.etag.length === 0 ||
      part.etag.length > 512
    ) {
      return bad("Each part needs a partNumber and an etag");
    }
    parts.push({ partNumber: part.partNumber, etag: part.etag });
  }

  return { ok: true, value: { parts } };
}
