import {
  HttpError,
  clipMetadata,
  completeClip,
  createClip,
  errorResponse,
  json,
  renameClip,
  serveVideo,
  uploadPart,
  watchPage,
} from "./handlers";

const methodNotAllowed = (allow: string) => errorResponse(405, "Method not allowed", { Allow: allow });

async function route(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
  const { pathname } = new URL(request.url);
  const method = request.method;
  let m: RegExpExecArray | null;

  if (pathname === "/") {
    return json({
      name: "chrono-clips",
      endpoints: {
        create: "POST /api/clips",
        uploadPart: "PUT /api/clips/:id/parts/:n",
        complete: "POST /api/clips/:id/complete",
        metadata: "GET /api/clips/:id",
        rename: "PATCH /api/clips/:id",
        watch: "GET /watch/:id",
        video: "GET /v/:id.mp4",
      },
    });
  }

  if (pathname === "/api/clips") {
    return method === "POST" ? createClip(request, env) : methodNotAllowed("POST");
  }

  if ((m = /^\/api\/clips\/([^/]+)\/parts\/([^/]+)$/.exec(pathname))) {
    return method === "PUT" ? uploadPart(request, env, m[1], m[2]) : methodNotAllowed("PUT");
  }

  if ((m = /^\/api\/clips\/([^/]+)\/complete$/.exec(pathname))) {
    return method === "POST" ? completeClip(request, env, m[1]) : methodNotAllowed("POST");
  }

  if ((m = /^\/api\/clips\/([^/]+)$/.exec(pathname))) {
    if (method === "GET") return clipMetadata(request, env, m[1]);
    if (method === "PATCH") return renameClip(request, env, m[1]);
    return methodNotAllowed("GET, PATCH");
  }

  if ((m = /^\/watch\/([^/]+)$/.exec(pathname))) {
    return method === "GET" || method === "HEAD" ? watchPage(request, env, ctx, m[1]) : methodNotAllowed("GET, HEAD");
  }

  if ((m = /^\/v\/([^/]+)\.mp4$/.exec(pathname))) {
    return method === "GET" || method === "HEAD" ? serveVideo(request, env, m[1]) : methodNotAllowed("GET, HEAD");
  }

  return errorResponse(404, "Not found");
}

export default {
  async fetch(request, env, ctx): Promise<Response> {
    try {
      return await route(request, env, ctx);
    } catch (err) {
      if (err instanceof HttpError) return errorResponse(err.status, err.message);
      console.error(JSON.stringify({ msg: "unhandled error", path: new URL(request.url).pathname, err: String(err) }));
      return errorResponse(500, "Internal error");
    }
  },
} satisfies ExportedHandler<Env>;
