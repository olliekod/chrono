import { isAuthorized } from "./auth";
import { countView, deleteClip, findClip, findReadyClip, insertClip, markReady, setTitle } from "./db";
import { renderWatchPage, watchPageCsp } from "./html";
import { isClipId, newClipId } from "./ids";
import { carriesOwnerToken, hashOwnerToken, newOwnerToken } from "./owner";
import { parseRange } from "./range";
import { MAX_PARTS, parseCompletion, parseNewClip, parseTitle } from "./validate";

export class HttpError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

export function json(data: unknown, status = 200, headers: HeadersInit = {}): Response {
  return Response.json(data, { status, headers });
}

export function errorResponse(status: number, message: string, headers: HeadersInit = {}): Response {
  return json({ error: message }, status, headers);
}

const MAX_JSON_BYTES = 16 * 1024;

/** Reads a small JSON body without trusting Content-Length, so a chunked upload can't fill memory. */
async function readJson(request: Request): Promise<unknown> {
  const chunks: Uint8Array[] = [];
  let total = 0;

  if (request.body) {
    const reader = request.body.getReader();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > MAX_JSON_BYTES) {
        await reader.cancel();
        throw new HttpError(413, "Request body too large");
      }
      chunks.push(value);
    }
  }

  try {
    return JSON.parse(await new Blob(chunks).text());
  } catch {
    throw new HttpError(400, "Body is not valid JSON");
  }
}

async function requireAuth(request: Request, env: Env): Promise<Response | null> {
  if (await isAuthorized(request, env.UPLOAD_KEY)) return null;
  return errorResponse(401, "Missing or invalid upload key", { "WWW-Authenticate": "Bearer" });
}

const notFound = () => errorResponse(404, "Clip not found");

// ---------------------------------------------------------------- uploading

/**
 * POST /api/clips: start an upload and get the id, part size and share link, plus the owner token. The token is shown
 * once, here: only its hash is kept, so it is the uploading app's proof that this clip is its own.
 */
export async function createClip(request: Request, env: Env): Promise<Response> {
  const denied = await requireAuth(request, env);
  if (denied) return denied;

  const parsed = parseNewClip(await readJson(request), Number(env.MAX_UPLOAD_BYTES));
  if (!parsed.ok) return errorResponse(parsed.status, parsed.error);
  const clip = parsed.value;

  const id = newClipId();
  const objectKey = `clips/${clip.owner}/${id}.mp4`;
  const ownerToken = newOwnerToken();

  const upload = await env.CLIPS.createMultipartUpload(objectKey, { httpMetadata: { contentType: "video/mp4" } });
  try {
    await insertClip(env.DB, id, objectKey, upload.uploadId, clip, await hashOwnerToken(ownerToken));
  } catch (err) {
    // Don't leave an orphaned multipart upload behind.
    await upload.abort().catch(() => undefined);
    throw err;
  }

  const origin = new URL(request.url).origin;
  return json({ id, partSize: Number(env.PART_SIZE_BYTES), link: `${origin}/watch/${id}`, ownerToken }, 201);
}

/** PUT /api/clips/:id/parts/:n: upload one part. Every part must be exactly partSize bytes except the last. */
export async function uploadPart(request: Request, env: Env, id: string, partText: string): Promise<Response> {
  const denied = await requireAuth(request, env);
  if (denied) return denied;

  const clip = isClipId(id) ? await findClip(env.DB, id) : null;
  if (!clip) return notFound();
  if (clip.status !== "uploading") return errorResponse(409, "This clip is already finished");

  const partSize = Number(env.PART_SIZE_BYTES);
  const totalParts = Math.ceil(clip.size_bytes / partSize);
  const partNumber = /^\d{1,5}$/.test(partText) ? Number(partText) : 0;
  if (partNumber < 1 || partNumber > totalParts || partNumber > MAX_PARTS) {
    return errorResponse(400, `Part number must be between 1 and ${totalParts}`);
  }

  const expected = partNumber < totalParts ? partSize : clip.size_bytes - (totalParts - 1) * partSize;
  if (!request.body) return errorResponse(400, "Empty part");

  // The stream errors unless exactly `expected` bytes flow through, whatever the client's headers claim.
  const { readable, writable } = new FixedLengthStream(expected);
  const piping = request.body.pipeTo(writable);

  try {
    const upload = env.CLIPS.resumeMultipartUpload(clip.object_key, clip.upload_id);
    const [part] = await Promise.all([upload.uploadPart(partNumber, readable), piping]);
    return json({ partNumber: part.partNumber, etag: part.etag });
  } catch (err) {
    console.warn(JSON.stringify({ msg: "part upload failed", id, partNumber, expected, err: String(err) }));
    return errorResponse(400, `Part ${partNumber} must be exactly ${expected} bytes`);
  }
}

/** POST /api/clips/:id/complete: stitch the parts together and publish the clip. */
export async function completeClip(request: Request, env: Env, id: string): Promise<Response> {
  const denied = await requireAuth(request, env);
  if (denied) return denied;

  const clip = isClipId(id) ? await findClip(env.DB, id) : null;
  if (!clip) return notFound();

  const origin = new URL(request.url).origin;
  const result = { id, link: `${origin}/watch/${id}`, size: clip.size_bytes };
  if (clip.status === "ready") return json(result);

  const parsed = parseCompletion(await readJson(request));
  if (!parsed.ok) return errorResponse(parsed.status, parsed.error);

  // R2 would happily finish with only some parts. Insist on all of them, so a client that
  // skipped one can retry instead of ending up with a truncated clip.
  const totalParts = Math.ceil(clip.size_bytes / Number(env.PART_SIZE_BYTES));
  const numbers = parsed.value.parts.map((part) => part.partNumber).sort((a, b) => a - b);
  if (numbers.length !== totalParts || numbers.some((n, i) => n !== i + 1)) {
    return errorResponse(400, `Expected exactly parts 1 to ${totalParts}`);
  }

  try {
    const upload = env.CLIPS.resumeMultipartUpload(clip.object_key, clip.upload_id);
    const object = await upload.complete(parsed.value.parts);
    if (object.size !== clip.size_bytes) {
      // Can't happen while part sizes are enforced, but never publish a clip that isn't what was declared.
      await env.CLIPS.delete(clip.object_key);
      await deleteClip(env.DB, id);
      return errorResponse(422, "Uploaded size does not match the declared size");
    }
  } catch (err) {
    // R2 may have finished on an earlier attempt whose database update failed; recover if so.
    const existing = await env.CLIPS.head(clip.object_key);
    if (existing?.size !== clip.size_bytes) {
      console.warn(JSON.stringify({ msg: "complete failed", id, err: String(err) }));
      return errorResponse(400, "Could not complete the upload. Check that every part was uploaded.");
    }
  }

  await markReady(env.DB, id);
  return json(result);
}

// ------------------------------------------------------------------ viewing

/** GET /api/clips/:id: public metadata (never storage internals). */
export async function clipMetadata(request: Request, env: Env, id: string): Promise<Response> {
  const clip = isClipId(id) ? await findReadyClip(env.DB, id) : null;
  if (!clip) return notFound();

  const origin = new URL(request.url).origin;
  return json({
    id: clip.id,
    owner: clip.owner,
    title: clip.title,
    filename: clip.filename,
    duration: clip.duration_seconds,
    resolution: clip.resolution,
    fps: clip.fps,
    bitrate: clip.bitrate_kbps,
    size: clip.size_bytes,
    views: clip.views,
    createdAt: clip.created_at,
    link: `${origin}/watch/${clip.id}`,
    video: `${origin}/v/${clip.id}.mp4`,
  });
}

const NOT_YOURS = "This clip belongs to someone else.";

/**
 * PATCH /api/clips/:id {title}: rename an uploaded clip. An empty title goes back to "<owner>'s clip".
 * Needs the upload key and, for any clip that has one, that clip's owner token. Clips from before owner tokens
 * existed have none and can still be renamed with the key alone.
 */
export async function renameClip(request: Request, env: Env, id: string): Promise<Response> {
  const denied = await requireAuth(request, env);
  if (denied) return denied;

  const clip = isClipId(id) ? await findReadyClip(env.DB, id) : null;
  if (!clip) return notFound();
  if (clip.owner_token_hash && !(await carriesOwnerToken(request, clip.owner_token_hash))) return errorResponse(403, NOT_YOURS);

  const body = await readJson(request);
  if (typeof body !== "object" || body === null || Array.isArray(body) || !("title" in body)) {
    return errorResponse(400, "Expected {\"title\": \"...\"}");
  }
  const title = parseTitle((body as { title: unknown }).title);
  if (!title.ok) return errorResponse(400, title.error);

  await setTitle(env.DB, id, title.value);
  return json({ id, title: title.value });
}

/**
 * DELETE /api/clips/:id: take a clip off the server, both its video and its page, so its link stops working.
 * Needs the upload key and this clip's owner token, which only the app that uploaded it has. That is what makes it
 * safe to share one key among friends: knowing the key and even a clip's id is not enough to remove someone else's.
 */
export async function removeClip(request: Request, env: Env, id: string): Promise<Response> {
  const denied = await requireAuth(request, env);
  if (denied) return denied;

  const clip = isClipId(id) ? await findClip(env.DB, id) : null;
  if (!clip) return notFound();

  if (!clip.owner_token_hash) {
    return errorResponse(403, "This clip was uploaded before removing uploads was supported, so it can't be removed from Chrono.");
  }
  if (!(await carriesOwnerToken(request, clip.owner_token_hash))) return errorResponse(403, NOT_YOURS);

  // The video first, then the row: if the first step fails the clip is still listed and the request can simply be
  // repeated (deleting an object that is already gone is not an error).
  if (clip.status === "uploading") {
    await env.CLIPS.resumeMultipartUpload(clip.object_key, clip.upload_id).abort().catch(() => undefined);
  }
  await env.CLIPS.delete(clip.object_key);
  await deleteClip(env.DB, id);

  return json({ id, removed: true });
}

/** GET /watch/:id: the shareable page, with the tags Discord reads to embed the video. */
export async function watchPage(request: Request, env: Env, ctx: ExecutionContext, id: string): Promise<Response> {
  const clip = isClipId(id) ? await findReadyClip(env.DB, id) : null;
  if (!clip) return notFound();

  ctx.waitUntil(countView(env.DB, id).catch((err) => console.warn(JSON.stringify({ msg: "view count failed", err: String(err) }))));

  const nonce = btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(16))));
  const html = renderWatchPage(clip, new URL(request.url).origin, nonce);

  return new Response(html, {
    headers: {
      "Content-Type": "text/html; charset=utf-8",
      "Content-Security-Policy": watchPageCsp(nonce),
      "X-Content-Type-Options": "nosniff",
      "Referrer-Policy": "no-referrer",
      "Cache-Control": "no-cache",
    },
  });
}

/** GET|HEAD /v/:id.mp4: the video itself, with Range support so players can seek. */
export async function serveVideo(request: Request, env: Env, id: string): Promise<Response> {
  const clip = isClipId(id) ? await findReadyClip(env.DB, id) : null;
  if (!clip) return notFound();

  const size = clip.size_bytes;
  const headers: Record<string, string> = {
    "Content-Type": "video/mp4",
    "Accept-Ranges": "bytes",
    "Cache-Control": "public, max-age=3600",
    "Content-Disposition": "inline",
    "X-Content-Type-Options": "nosniff",
    "Cross-Origin-Resource-Policy": "cross-origin",
  };

  const range = parseRange(request.headers.get("Range"), size);
  if (range === "unsatisfiable") {
    return new Response(null, { status: 416, headers: { "Content-Range": `bytes */${size}` } });
  }

  if (request.method === "HEAD") {
    return new Response(null, { headers: { ...headers, "Content-Length": String(size) } });
  }

  const object = await env.CLIPS.get(clip.object_key, range ? { range } : undefined);
  if (!object) return notFound();

  if (range) {
    return new Response(object.body, {
      status: 206,
      headers: {
        ...headers,
        "Content-Range": `bytes ${range.offset}-${range.offset + range.length - 1}/${size}`,
        "Content-Length": String(range.length),
      },
    });
  }

  return new Response(object.body, { headers: { ...headers, "Content-Length": String(size), ETag: object.httpEtag } });
}
