import { exports } from "cloudflare:workers";

export const BASE = "https://clips.test";
export const PART = 5 * 1024 * 1024; // matches PART_SIZE_BYTES in vitest.config.ts
export const AUTH = { Authorization: "Bearer test-upload-key" };

export function api(path: string, init: RequestInit = {}): Promise<Response> {
  return exports.default.fetch(new Request(BASE + path, init));
}

export function jsonPost(path: string, body: unknown, headers: Record<string, string> = AUTH): Promise<Response> {
  return api(path, {
    method: "POST",
    headers: { ...headers, "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

/** Deterministic, non-repeating-at-small-scale content so range reads can be checked byte for byte. */
export function makeData(size: number): Uint8Array {
  const data = new Uint8Array(size);
  for (let i = 0; i < size; i++) data[i] = (i * 31 + (i >> 8)) & 0xff;
  return data;
}

/** Byte-for-byte comparison that stays fast on multi-megabyte buffers (toEqual is per element). */
export function sameBytes(actual: ArrayBuffer | Uint8Array, expected: Uint8Array): boolean {
  const a = actual instanceof Uint8Array ? actual : new Uint8Array(actual);
  if (a.length !== expected.length) return false;
  for (let i = 0; i < a.length; i++) {
    if (a[i] !== expected[i]) return false;
  }
  return true;
}

export interface UploadedClip {
  id: string;
  link: string;
  data: Uint8Array;
}

/** Runs the whole create -> parts -> complete flow. */
export async function uploadClip(
  size = PART * 2 + 12_345,
  meta: Record<string, unknown> = {},
): Promise<UploadedClip> {
  const data = makeData(size);

  const created = await jsonPost("/api/clips", {
    username: "olly",
    filename: "clip.mp4",
    size,
    ...meta,
  });
  if (created.status !== 201) throw new Error(`create failed: ${created.status} ${await created.text()}`);
  const { id, partSize, link } = await created.json<{ id: string; partSize: number; link: string }>();

  const parts: { partNumber: number; etag: string }[] = [];
  for (let offset = 0, n = 1; offset < size; offset += partSize, n++) {
    const res = await api(`/api/clips/${id}/parts/${n}`, {
      method: "PUT",
      headers: AUTH,
      body: data.slice(offset, offset + partSize),
    });
    if (res.status !== 200) throw new Error(`part ${n} failed: ${res.status} ${await res.text()}`);
    parts.push(await res.json<{ partNumber: number; etag: string }>());
  }

  const done = await jsonPost(`/api/clips/${id}/complete`, { parts });
  if (done.status !== 200) throw new Error(`complete failed: ${done.status} ${await done.text()}`);

  return { id, link, data };
}
