import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import { AUTH, BASE, PART, api, jsonPost, sameBytes, uploadClip } from "./helpers";

const SIZE = PART + 20_000;

describe("GET /watch/:id", () => {
  it("serves a page with the embed tags Discord reads", async () => {
    const { id } = await uploadClip(SIZE, { duration: 65, resolution: "1920x1080", fps: 60 });

    const res = await api(`/watch/${id}`);
    const html = await res.text();

    expect(res.status).toBe(200);
    expect(res.headers.get("content-type")).toContain("text/html");
    expect(html).toContain(`<meta property="og:type" content="video.other">`);
    expect(html).toContain(`<meta property="og:video" content="${BASE}/v/${id}.mp4">`);
    expect(html).toContain(`<meta property="og:video:secure_url" content="${BASE}/v/${id}.mp4">`);
    expect(html).toContain(`<meta property="og:video:type" content="video/mp4">`);
    expect(html).toContain(`<meta property="og:video:width" content="1920">`);
    expect(html).toContain(`<meta property="og:video:height" content="1080">`);
    expect(html).toContain("olly");
  });

  it("locks the page down with a content security policy", async () => {
    const { id } = await uploadClip(1000);

    const res = await api(`/watch/${id}`);
    const csp = res.headers.get("content-security-policy") ?? "";

    expect(csp).toContain("default-src 'none'");
    expect(csp).toContain("media-src 'self'");
    expect(csp).not.toContain("unsafe-inline");
  });

  it("counts views", async () => {
    const { id } = await uploadClip(1000);
    await api(`/watch/${id}`);

    // The increment runs after the response, so give it a moment.
    let views = 0;
    for (let i = 0; i < 20 && views === 0; i++) {
      const row = await env.DB.prepare("SELECT views FROM clips WHERE id = ?").bind(id).first<{ views: number }>();
      views = row?.views ?? 0;
      if (views === 0) await new Promise((r) => setTimeout(r, 50));
    }
    expect(views).toBe(1);
  });

  it("404s for unknown clips", async () => {
    expect((await api("/watch/aaaaaaaaaaaa")).status).toBe(404);
  });

  it("404s for clips that are still uploading", async () => {
    const created = await jsonPost("/api/clips", { username: "olly", size: 100 });
    const { id } = await created.json<{ id: string }>();

    expect((await api(`/watch/${id}`)).status).toBe(404);
    expect((await api(`/v/${id}.mp4`)).status).toBe(404);
  });

  it("404s for malformed ids without touching the database", async () => {
    expect((await api("/watch/..%2F..%2Fetc")).status).toBe(404);
    expect((await api("/watch/x")).status).toBe(404);
  });
});

describe("GET /v/:id.mp4", () => {
  it("streams the whole clip", async () => {
    const { id, data } = await uploadClip(SIZE);

    const res = await api(`/v/${id}.mp4`);

    expect(res.status).toBe(200);
    expect(res.headers.get("content-type")).toBe("video/mp4");
    expect(res.headers.get("accept-ranges")).toBe("bytes");
    expect(res.headers.get("content-length")).toBe(String(SIZE));
    expect(sameBytes(await res.arrayBuffer(), data)).toBe(true);
  });

  it("serves a byte range with 206", async () => {
    const { id, data } = await uploadClip(SIZE);

    const res = await api(`/v/${id}.mp4`, { headers: { Range: "bytes=100-199" } });

    expect(res.status).toBe(206);
    expect(res.headers.get("content-range")).toBe(`bytes 100-199/${SIZE}`);
    expect(res.headers.get("content-length")).toBe("100");
    expect(new Uint8Array(await res.arrayBuffer())).toEqual(data.slice(100, 200));
  });

  it("serves an open-ended range", async () => {
    const { id, data } = await uploadClip(SIZE);

    const res = await api(`/v/${id}.mp4`, { headers: { Range: `bytes=${SIZE - 10}-` } });

    expect(res.status).toBe(206);
    expect(res.headers.get("content-range")).toBe(`bytes ${SIZE - 10}-${SIZE - 1}/${SIZE}`);
    expect(new Uint8Array(await res.arrayBuffer())).toEqual(data.slice(SIZE - 10));
  });

  it("serves a suffix range", async () => {
    const { id, data } = await uploadClip(SIZE);

    const res = await api(`/v/${id}.mp4`, { headers: { Range: "bytes=-10" } });

    expect(res.status).toBe(206);
    expect(new Uint8Array(await res.arrayBuffer())).toEqual(data.slice(SIZE - 10));
  });

  it("answers 416 for a range past the end", async () => {
    const { id } = await uploadClip(SIZE);

    const res = await api(`/v/${id}.mp4`, { headers: { Range: `bytes=${SIZE + 100}-${SIZE + 200}` } });

    expect(res.status).toBe(416);
    expect(res.headers.get("content-range")).toBe(`bytes */${SIZE}`);
  });

  it("answers HEAD with headers and no body", async () => {
    const { id } = await uploadClip(SIZE);

    const res = await api(`/v/${id}.mp4`, { method: "HEAD" });

    expect(res.status).toBe(200);
    expect(res.headers.get("content-length")).toBe(String(SIZE));
    expect(await res.text()).toBe("");
  });

  it("404s for unknown clips", async () => {
    expect((await api("/v/aaaaaaaaaaaa.mp4")).status).toBe(404);
  });
});

describe("GET /api/clips/:id", () => {
  it("returns public metadata and hides storage internals", async () => {
    const { id } = await uploadClip(SIZE, { duration: 30, resolution: "1280x720", fps: 30, bitrate: 4000 });

    const res = await api(`/api/clips/${id}`);
    const body = await res.json<Record<string, unknown>>();

    expect(res.status).toBe(200);
    expect(body).toMatchObject({ id, owner: "olly", duration: 30, resolution: "1280x720", fps: 30, size: SIZE });
    expect(body).not.toHaveProperty("upload_id");
    expect(body).not.toHaveProperty("object_key");
    expect(body).not.toHaveProperty("uploadId");
    expect(body).not.toHaveProperty("objectKey");
  });

  it("404s for unknown clips", async () => {
    expect((await api("/api/clips/aaaaaaaaaaaa")).status).toBe(404);
  });
});

describe("routing", () => {
  it("describes itself at /", async () => {
    const res = await api("/");
    expect(res.status).toBe(200);
    expect(await res.json()).toHaveProperty("name", "chrono-clips");
  });

  it("404s unknown routes", async () => {
    expect((await api("/nope")).status).toBe(404);
  });

  it("405s wrong methods on known routes", async () => {
    expect((await api("/api/clips", { method: "GET", headers: AUTH })).status).toBe(405);
    expect((await api("/watch/aaaaaaaaaaaa", { method: "POST" })).status).toBe(405);
  });
});
