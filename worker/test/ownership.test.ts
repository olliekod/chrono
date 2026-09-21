import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import { hashOwnerToken, newOwnerToken } from "../src/owner";
import { AUTH, PART, api, asOwner, jsonPost, uploadClip } from "./helpers";

const SIZE = PART + 20_000;

const remove = (id: string, headers: Record<string, string>) => api(`/api/clips/${id}`, { method: "DELETE", headers });
const rename = (id: string, title: string, headers: Record<string, string>) =>
  api(`/api/clips/${id}`, { method: "PATCH", headers: { ...headers, "Content-Type": "application/json" }, body: JSON.stringify({ title }) });

const row = (id: string) => env.DB.prepare("SELECT * FROM clips WHERE id = ?").bind(id).first<Record<string, unknown>>();

/** A clip that was uploaded before owner tokens existed: it has no hash. */
async function legacyClip(): Promise<string> {
  const clip = await uploadClip(SIZE, { title: "Old" });
  await env.DB.prepare("UPDATE clips SET owner_token_hash = NULL WHERE id = ?").bind(clip.id).run();
  return clip.id;
}

describe("owner tokens", () => {
  it("are handed to the uploader when the upload starts, and are long and different every time", async () => {
    const first = await uploadClip(1000);
    const second = await uploadClip(1000);

    expect(first.ownerToken).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(second.ownerToken).not.toBe(first.ownerToken);
  });

  it("are never stored, only their hash", async () => {
    const clip = await uploadClip(1000);
    const stored = await row(clip.id);

    expect(JSON.stringify(stored)).not.toContain(clip.ownerToken);
    expect(stored?.owner_token_hash).toBe(await hashOwnerToken(clip.ownerToken));
    expect(stored?.owner_token_hash).toMatch(/^[0-9a-f]{64}$/);
  });

  it("never appear on the public metadata or the watch page", async () => {
    const clip = await uploadClip(SIZE);
    const hash = String((await row(clip.id))?.owner_token_hash);

    const meta = await (await api(`/api/clips/${clip.id}`)).text();
    const page = await (await api(`/watch/${clip.id}`)).text();

    for (const leaked of [clip.ownerToken, hash]) {
      expect(meta).not.toContain(leaked);
      expect(page).not.toContain(leaked);
    }
  });

  it("come from the crypto random source, so two fresh ones never match", () => {
    const tokens = new Set(Array.from({ length: 200 }, () => newOwnerToken()));
    expect(tokens.size).toBe(200);
  });
});

describe("DELETE /api/clips/:id (remove an upload)", () => {
  it("removes the page, the video, the stored file and the row", async () => {
    const clip = await uploadClip(SIZE);
    const objectKey = String((await row(clip.id))?.object_key);
    expect(await env.CLIPS.head(objectKey)).not.toBeNull();

    const res = await remove(clip.id, asOwner(clip));

    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ id: clip.id, removed: true });
    expect((await api(`/watch/${clip.id}`)).status).toBe(404);
    expect((await api(`/v/${clip.id}.mp4`)).status).toBe(404);
    expect((await api(`/api/clips/${clip.id}`)).status).toBe(404);
    expect(await env.CLIPS.head(objectKey)).toBeNull();
    expect(await row(clip.id)).toBeNull();
  });

  it("needs the upload key", async () => {
    const clip = await uploadClip(1000);

    expect((await remove(clip.id, { "X-Owner-Token": clip.ownerToken })).status).toBe(401);
    expect((await remove(clip.id, { Authorization: "Bearer wrong", "X-Owner-Token": clip.ownerToken })).status).toBe(401);
    expect((await api(`/watch/${clip.id}`)).status).toBe(200);   // still there
  });

  it("is refused without the clip's own token, even for someone who has the key and the id", async () => {
    const mine = await uploadClip(1000);

    for (const headers of [AUTH, { ...AUTH, "X-Owner-Token": "" }, { ...AUTH, "X-Owner-Token": "not-a-token" }, { ...AUTH, "X-Owner-Token": "A".repeat(43) }]) {
      const res = await remove(mine.id, headers);
      expect(res.status).toBe(403);
      expect((await res.json<{ error: string }>()).error).toContain("someone else");
    }
    expect((await api(`/watch/${mine.id}`)).status).toBe(200);
    expect(await row(mine.id)).not.toBeNull();
  });

  it("is refused with another clip's token: a friend can't remove your clip with theirs", async () => {
    const mine = await uploadClip(1000);
    const theirs = await uploadClip(1000);

    const res = await remove(mine.id, asOwner(theirs));

    expect(res.status).toBe(403);
    expect((await api(`/watch/${mine.id}`)).status).toBe(200);
    expect((await api(`/watch/${theirs.id}`)).status).toBe(200);
  });

  it("removes only that clip", async () => {
    const keep = await uploadClip(1000);
    const drop = await uploadClip(1000);

    await remove(drop.id, asOwner(drop));

    expect((await api(`/watch/${keep.id}`)).status).toBe(200);
    expect((await api(`/v/${keep.id}.mp4`, { headers: { Range: "bytes=0-9" } })).status).toBe(206);
  });

  it("404s for an unknown clip, and the second try after a removal", async () => {
    const clip = await uploadClip(1000);

    expect((await remove("abcdefghijkl", asOwner(clip))).status).toBe(404);
    expect((await remove("short", asOwner(clip))).status).toBe(404);

    expect((await remove(clip.id, asOwner(clip))).status).toBe(200);
    expect((await remove(clip.id, asOwner(clip))).status).toBe(404);   // already gone; the app treats this as done
  });

  it("can remove an upload that was never finished, and cleans up what it had received", async () => {
    const created = await jsonPost("/api/clips", { username: "olly", size: 100 });
    const { id, ownerToken } = await created.json<{ id: string; ownerToken: string }>();
    expect((await row(id))?.status).toBe("uploading");

    const res = await remove(id, { ...AUTH, "X-Owner-Token": ownerToken });

    expect(res.status).toBe(200);
    expect(await row(id)).toBeNull();
  });

  it("cannot remove a clip from before owner tokens existed, and says why", async () => {
    const id = await legacyClip();

    const res = await remove(id, { ...AUTH, "X-Owner-Token": "A".repeat(43) });

    expect(res.status).toBe(403);
    expect((await res.json<{ error: string }>()).error).toContain("before removing uploads was supported");
    expect((await api(`/watch/${id}`)).status).toBe(200);
  });

  it("accepts only DELETE, GET and PATCH on a clip", async () => {
    const clip = await uploadClip(1000);
    const res = await api(`/api/clips/${clip.id}`, { method: "PUT", headers: AUTH });

    expect(res.status).toBe(405);
    expect(res.headers.get("allow")).toBe("GET, PATCH, DELETE");
  });

  it("is not offered on the collection", async () => {
    expect((await api("/api/clips", { method: "DELETE", headers: AUTH })).status).toBe(405);
  });
});

describe("renaming and owner tokens", () => {
  it("works with the clip's own token", async () => {
    const clip = await uploadClip(SIZE);

    const res = await rename(clip.id, "Mine", asOwner(clip));

    expect(res.status).toBe(200);
    expect(await (await api(`/watch/${clip.id}`)).text()).toContain("<title>Mine</title>");
  });

  it("is refused without it, or with someone else's", async () => {
    const mine = await uploadClip(SIZE, { title: "Mine" });
    const theirs = await uploadClip(SIZE);

    expect((await rename(mine.id, "Taken", AUTH)).status).toBe(403);
    expect((await rename(mine.id, "Taken", asOwner(theirs))).status).toBe(403);
    expect(await (await api(`/watch/${mine.id}`)).text()).toContain("<title>Mine</title>");
  });

  it("still works with the key alone for a clip from before owner tokens existed", async () => {
    const id = await legacyClip();

    expect((await rename(id, "Still renameable", AUTH)).status).toBe(200);
  });
});
