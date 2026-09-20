import type { ClipRow } from "./db";

export function escapeHtml(text: string): string {
  return text
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

/** 65 -> "1:05", 3725 -> "1:02:05" */
export function formatDuration(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const ss = String(s).padStart(2, "0");
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${ss}` : `${m}:${ss}`;
}

function dimensions(resolution: string | null): { width: number; height: number } | null {
  const match = /^(\d{2,5})x(\d{2,5})$/.exec(resolution ?? "");
  return match ? { width: Number(match[1]), height: Number(match[2]) } : null;
}

/** "1080p60", or just "1080p" when the frame rate is unknown. */
function qualityLabel(clip: ClipRow): string | null {
  const dims = dimensions(clip.resolution);
  if (!dims) return null;
  return `${dims.height}p${clip.fps ?? ""}`;
}

export function watchPageCsp(nonce: string): string {
  return [
    "default-src 'none'",
    "media-src 'self'",
    `style-src 'nonce-${nonce}'`,
    `script-src 'nonce-${nonce}'`,
    "base-uri 'none'",
    "form-action 'none'",
    "frame-ancestors 'none'",
  ].join("; ");
}

export function renderWatchPage(clip: ClipRow, origin: string, nonce: string): string {
  const e = escapeHtml;
  const pageUrl = `${origin}/watch/${clip.id}`;
  const videoUrl = `${origin}/v/${clip.id}.mp4`;
  const dims = dimensions(clip.resolution);
  const quality = qualityLabel(clip);

  const facts = [
    clip.duration_seconds ? formatDuration(clip.duration_seconds) : null,
    quality,
    `${clip.views} ${clip.views === 1 ? "view" : "views"}`,
  ].filter((part): part is string => part !== null);

  const description = [clip.filename, ...facts].join(" • ");

  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${e(clip.owner)}'s clip</title>
<meta property="og:site_name" content="Chrono">
<meta property="og:type" content="video.other">
<meta property="og:title" content="${e(clip.owner)}'s clip">
<meta property="og:description" content="${e(description)}">
<meta property="og:url" content="${e(pageUrl)}">
<meta property="og:video" content="${e(videoUrl)}">
<meta property="og:video:url" content="${e(videoUrl)}">
<meta property="og:video:secure_url" content="${e(videoUrl)}">
<meta property="og:video:type" content="video/mp4">${
    dims
      ? `
<meta property="og:video:width" content="${dims.width}">
<meta property="og:video:height" content="${dims.height}">`
      : ""
  }
<meta name="twitter:card" content="player">
<style nonce="${nonce}">
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body { background: #0e0e10; color: #efeff1; font: 15px/1.4 system-ui, -apple-system, "Segoe UI", sans-serif;
         min-height: 100vh; display: grid; place-items: center; padding: 20px; }
  main { width: 100%; max-width: 1200px; }
  video { width: 100%; max-height: 80vh; background: #000; border-radius: 8px; display: block; }
  .info { margin-top: 16px; padding: 16px 20px; background: #18181b; border-radius: 8px;
          display: flex; gap: 16px; align-items: center; justify-content: space-between; flex-wrap: wrap; }
  h1 { font-size: 17px; }
  .facts { color: #adadb8; font-size: 13px; margin-top: 4px; }
  button { padding: 10px 18px; background: #9147ff; color: #fff; border: 0; border-radius: 6px;
           font: inherit; font-weight: 600; cursor: pointer; }
  button:hover { background: #772ce8; }
</style>
</head>
<body>
<main>
  <video controls autoplay muted loop playsinline preload="metadata" src="${e(videoUrl)}"></video>
  <div class="info">
    <div>
      <h1>${e(clip.owner)}</h1>
      <div class="facts">${e(description)}</div>
    </div>
    <button id="copy" type="button">Copy link</button>
  </div>
</main>
<script nonce="${nonce}">
  const button = document.getElementById("copy");
  button.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(location.href);
      button.textContent = "Copied!";
    } catch {
      button.textContent = "Copy failed";
    }
    setTimeout(() => { button.textContent = "Copy link"; }, 2000);
  });
</script>
</body>
</html>`;
}
