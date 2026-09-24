-- A diagnostics report someone chose to send when they saved a clip (Settings > App > Send a diagnostics report when
-- you save a clip). Plain text, the same report the app's own Copy button produces; nothing here is a clip or touches
-- R2. Capped in size at the application layer, not here.
CREATE TABLE diagnostics_reports (
  id             TEXT PRIMARY KEY,
  owner          TEXT NOT NULL,
  clip_filename  TEXT,
  report         TEXT NOT NULL,
  created_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

CREATE INDEX idx_diagnostics_created ON diagnostics_reports (created_at DESC);
