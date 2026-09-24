import type { NewClip } from "./validate";

export interface ClipRow {
  id: string;
  owner: string;
  filename: string;
  title: string | null;
  object_key: string;
  upload_id: string;
  status: "uploading" | "ready";
  size_bytes: number;
  duration_seconds: number | null;
  resolution: string | null;
  fps: number | null;
  bitrate_kbps: number | null;
  views: number;
  created_at: string;
  /** SHA-256 of the owner token. Null for clips uploaded before owner tokens existed. */
  owner_token_hash: string | null;
}

export function findClip(db: D1Database, id: string): Promise<ClipRow | null> {
  return db.prepare("SELECT * FROM clips WHERE id = ?").bind(id).first<ClipRow>();
}

export function findReadyClip(db: D1Database, id: string): Promise<ClipRow | null> {
  return db.prepare("SELECT * FROM clips WHERE id = ? AND status = 'ready'").bind(id).first<ClipRow>();
}

export async function insertClip(
  db: D1Database,
  id: string,
  objectKey: string,
  uploadId: string,
  clip: NewClip,
  ownerTokenHash: string,
): Promise<void> {
  await db
    .prepare(
      `INSERT INTO clips (id, owner, filename, title, object_key, upload_id, size_bytes, duration_seconds, resolution, fps, bitrate_kbps, owner_token_hash)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    )
    .bind(id, clip.owner, clip.filename, clip.title, objectKey, uploadId, clip.size, clip.duration, clip.resolution, clip.fps, clip.bitrate, ownerTokenHash)
    .run();
}

export async function setTitle(db: D1Database, id: string, title: string | null): Promise<void> {
  await db.prepare("UPDATE clips SET title = ? WHERE id = ?").bind(title, id).run();
}

export async function markReady(db: D1Database, id: string): Promise<void> {
  await db.prepare("UPDATE clips SET status = 'ready' WHERE id = ?").bind(id).run();
}

export async function deleteClip(db: D1Database, id: string): Promise<void> {
  await db.prepare("DELETE FROM clips WHERE id = ?").bind(id).run();
}

export async function countView(db: D1Database, id: string): Promise<void> {
  await db.prepare("UPDATE clips SET views = views + 1 WHERE id = ?").bind(id).run();
}

export interface DiagnosticsRow {
  id: string;
  owner: string;
  clip_filename: string | null;
  report: string;
  created_at: string;
}

export async function insertDiagnosticsReport(
  db: D1Database,
  id: string,
  owner: string,
  clipFilename: string | null,
  report: string,
): Promise<void> {
  await db
    .prepare("INSERT INTO diagnostics_reports (id, owner, clip_filename, report) VALUES (?, ?, ?, ?)")
    .bind(id, owner, clipFilename, report)
    .run();
}

/** Newest first, capped at `limit` (the caller has already validated it's in range). */
export function listDiagnosticsReports(db: D1Database, limit: number): Promise<D1Result<DiagnosticsRow>> {
  return db.prepare("SELECT * FROM diagnostics_reports ORDER BY created_at DESC LIMIT ?").bind(limit).run<DiagnosticsRow>();
}
