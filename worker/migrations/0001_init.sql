-- A clip is created as 'uploading' and becomes 'ready' once its multipart upload is completed.
-- Only 'ready' clips are visible through /watch, /v and /api/clips/:id.
CREATE TABLE clips (
  id               TEXT PRIMARY KEY,
  owner            TEXT NOT NULL,
  filename         TEXT NOT NULL,
  object_key       TEXT NOT NULL,
  upload_id        TEXT NOT NULL,
  status           TEXT NOT NULL DEFAULT 'uploading' CHECK (status IN ('uploading', 'ready')),
  size_bytes       INTEGER NOT NULL,
  duration_seconds REAL,
  resolution       TEXT,
  fps              INTEGER,
  bitrate_kbps     INTEGER,
  views            INTEGER NOT NULL DEFAULT 0,
  created_at       TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

CREATE INDEX idx_clips_owner_created ON clips (owner, created_at DESC);
