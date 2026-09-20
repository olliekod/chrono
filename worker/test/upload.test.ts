import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import { AUTH, BASE, PART, api, jsonPost, makeData, uploadClip } from "./helpers";

describe("authentication", () => {
  it("rejects clip creation without a key", async () => {
    const res = await jsonPost("/api/clips", { username: "olly", size: 10 }, {});
    expect(res.status).toBe(401);
  });

  it("rejects clip creation with the wrong key", async () => {
    const res = await jsonPost("/api/clips", { username: "olly", size: 10 }, { Authorization: "Bearer nope" });
    expect(res.status).toBe(401);
  });

  it("rejects part uploads and completion without a key", async () => {
    const part = await api("/api/clips/aaaaaaaaaaaa/parts/1", { method: "PUT", body: "x" });
    const done = await jsonPost("/api/clips/aaaaaaaaaaaa/complete", { parts: [] }, {});
    expect(part.status).toBe(401);
    expect(done.status).toBe(401);
  });
});

describe("POST /api/clips", () => {
  it("creates an upload and tells the client the part size and share link", async () => {
    const res = await jsonPost("/api/clips", {
      username: "olly",
      filename: "clip.mp4",
      size: 1234,
      duration: 30.5,
      resolution: "1920x1080",
      fps: 60,
      bitrate: 8000,
    });

    expect(res.status).toBe(201);
    const body = await res.json<{ id: string; partSize: number; link: string }>();
    expect(body.id).toMatch(/^[A-Za-z0-9_-]{12}$/);
    expect(body.partSize).toBe(PART);
    expect(body.link).toBe(`${BASE}/watch/${body.id}`);
  });

  it("gives every clip a different id", async () => {
    const ids = new Set<string>();
    for (let i = 0; i < 20; i++) {
      const res = await jsonPost("/api/clips", { username: "olly", size: 10 });
      ids.add((await res.json<{ id: string }>()).id);
    }
    expect(ids.size).toBe(20);
  });

  it.each([
    ["path traversal in username", { username: "../x", size: 10 }],
    ["empty username", { username: "", size: 10 }],
    ["overlong username", { username: "a".repeat(33), size: 10 }],
    ["missing size", { username: "olly" }],
    ["zero size", { username: "olly", size: 0 }],
    ["fractional size", { username: "olly", size: 1.5 }],
    ["non-mp4 filename", { username: "olly", size: 10, filename: "clip.exe" }],
    ["bad resolution", { username: "olly", size: 10, resolution: "<b>big</b>" }],
    ["negative fps", { username: "olly", size: 10, fps: -1 }],
  ])("rejects %s", async (_name, body) => {
    const res = await jsonPost("/api/clips", body);
    expect(res.status).toBe(400);
  });

  it("rejects clips larger than the limit", async () => {
    const res = await jsonPost("/api/clips", { username: "olly", size: 21 * 1024 * 1024 });
    expect(res.status).toBe(413);
  });

  it("rejects malformed JSON", async () => {
    const res = await api("/api/clips", {
      method: "POST",
      headers: { ...AUTH, "Content-Type": "application/json" },
      body: "{not json",
    });
    expect(res.status).toBe(400);
  });

  it("sanitises the stored filename", async () => {
    const res = await jsonPost("/api/clips", { username: "olly", size: 10, filename: "<script>alert(1)</script>.mp4" });
    const { id } = await res.json<{ id: string }>();

    const row = await env.DB.prepare("SELECT filename FROM clips WHERE id = ?").bind(id).first<{ filename: string }>();

    expect(row?.filename).not.toMatch(/[<>()]/);
    expect(row?.filename.endsWith(".mp4")).toBe(true);
  });
});

describe("multipart upload", () => {
  it("uploads a multi-part clip and marks it ready", async () => {
    const size = PART * 2 + 12_345;
    const { id } = await uploadClip(size, { duration: 12, resolution: "1280x720", fps: 30, bitrate: 4000 });

    const row = await env.DB.prepare("SELECT status, size_bytes, owner, resolution, fps FROM clips WHERE id = ?")
      .bind(id)
      .first();

    expect(row).toMatchObject({ status: "ready", size_bytes: size, owner: "olly", resolution: "1280x720", fps: 30 });
  });

  it("uploads a clip smaller than one part", async () => {
    const { id } = await uploadClip(1000);
    const row = await env.DB.prepare("SELECT status FROM clips WHERE id = ?").bind(id).first();
    expect(row?.status).toBe("ready");
  });

  async function startUpload(size: number) {
    const res = await jsonPost("/api/clips", { username: "olly", size });
    return (await res.json<{ id: string }>()).id;
  }

  it("rejects a non-final part of the wrong size", async () => {
    const id = await startUpload(PART * 2 + 100);
    const res = await api(`/api/clips/${id}/parts/1`, { method: "PUT", headers: AUTH, body: makeData(PART - 1) });
    expect(res.status).toBe(400);
  });

  it("rejects a final part of the wrong size", async () => {
    const id = await startUpload(PART + 100);
    const res = await api(`/api/clips/${id}/parts/2`, { method: "PUT", headers: AUTH, body: makeData(101) });
    expect(res.status).toBe(400);
  });

  it.each([0, 3, 99, 10_001])("rejects part number %i", async (n) => {
    const id = await startUpload(PART + 100);
    const res = await api(`/api/clips/${id}/parts/${n}`, { method: "PUT", headers: AUTH, body: makeData(PART) });
    expect(res.status).toBe(400);
  });

  it("returns 404 for parts of an unknown clip", async () => {
    const res = await api("/api/clips/aaaaaaaaaaaa/parts/1", { method: "PUT", headers: AUTH, body: makeData(10) });
    expect(res.status).toBe(404);
  });

  it("refuses to complete with a missing part and leaves the clip unpublished", async () => {
    const id = await startUpload(PART + 100);
    const first = await api(`/api/clips/${id}/parts/1`, { method: "PUT", headers: AUTH, body: makeData(PART) });
    const part1 = await first.json<{ partNumber: number; etag: string }>();

    const done = await jsonPost(`/api/clips/${id}/complete`, { parts: [part1] });

    expect(done.status).toBe(400);
    const row = await env.DB.prepare("SELECT status FROM clips WHERE id = ?").bind(id).first();
    expect(row?.status).toBe("uploading");
  });

  it("refuses to complete with a bogus etag", async () => {
    const id = await startUpload(1000);
    await api(`/api/clips/${id}/parts/1`, { method: "PUT", headers: AUTH, body: makeData(1000) });

    const done = await jsonPost(`/api/clips/${id}/complete`, { parts: [{ partNumber: 1, etag: "deadbeef" }] });

    expect(done.status).toBe(400);
  });

  it("refuses malformed completion bodies", async () => {
    const id = await startUpload(1000);
    for (const body of [{}, { parts: "x" }, { parts: [{ partNumber: "1", etag: 5 }] }]) {
      const done = await jsonPost(`/api/clips/${id}/complete`, body);
      expect(done.status).toBe(400);
    }
  });

  it("completing twice is harmless and returns the same link", async () => {
    const { id, link } = await uploadClip(1000);

    const again = await jsonPost(`/api/clips/${id}/complete`, { parts: [] });

    expect(again.status).toBe(200);
    expect((await again.json<{ link: string }>()).link).toBe(link);
  });

  it("refuses more parts once the clip is ready", async () => {
    const { id } = await uploadClip(1000);
    const res = await api(`/api/clips/${id}/parts/1`, { method: "PUT", headers: AUTH, body: makeData(1000) });
    expect(res.status).toBe(409);
  });
});
