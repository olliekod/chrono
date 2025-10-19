import sqlite3
from contextlib import contextmanager
from config import settings
import json
from datetime import datetime

DB_FILE = "clips.db"

# Database schema
SCHEMA_SQL = """
CREATE TABLE IF NOT EXISTS clips (
    id TEXT PRIMARY KEY,
    owner TEXT NOT NULL,
    filename TEXT NOT NULL,
    storage_path TEXT NOT NULL,
    duration_seconds REAL,
    file_size_bytes INTEGER,
    resolution TEXT,
    fps INTEGER,
    bitrate_kbps INTEGER,
    thumbnail_path TEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    views INTEGER DEFAULT 0,
    settings TEXT
);

CREATE INDEX IF NOT EXISTS idx_owner ON clips(owner);
CREATE INDEX IF NOT EXISTS idx_created_at ON clips(created_at);
"""

@contextmanager
def get_db():
    """Context manager for database connections"""
    conn = sqlite3.connect(DB_FILE)
    conn.row_factory = sqlite3.Row  # Return rows as dictionaries
    try:
        yield conn
        conn.commit()
    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()

def init_db():
    """Initialize database schema"""
    with get_db() as conn:
        conn.executescript(SCHEMA_SQL)
        print("✓ Database initialized successfully")

def insert_clip(clip_data: dict) -> str:
    """Insert new clip record, returns clip ID"""
    with get_db() as conn:
        cursor = conn.cursor()
        
        # Convert settings dict to JSON string
        settings_json = json.dumps(clip_data.get('settings', {}))
        
        cursor.execute("""
            INSERT INTO clips (
                id, owner, filename, storage_path, duration_seconds,
                file_size_bytes, resolution, fps, bitrate_kbps, settings
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """, (
            clip_data['id'],
            clip_data['owner'],
            clip_data['filename'],
            clip_data['storage_path'],
            clip_data.get('duration'),
            clip_data.get('file_size'),
            clip_data.get('resolution'),
            clip_data.get('fps'),
            clip_data.get('bitrate'),
            settings_json
        ))
        
        return clip_data['id']

def get_clip(clip_id: str) -> dict:
    """Retrieve clip metadata by ID"""
    with get_db() as conn:
        cursor = conn.cursor()
        cursor.execute("SELECT * FROM clips WHERE id = ?", (clip_id,))
        row = cursor.fetchone()
        
        if row:
            # Convert Row to dict
            clip = dict(row)
            # Parse JSON settings back to dict
            if clip.get('settings'):
                clip['settings'] = json.loads(clip['settings'])
            return clip
        return None

def increment_views(clip_id: str):
    """Increment view counter for a clip"""
    with get_db() as conn:
        cursor = conn.cursor()
        cursor.execute("UPDATE clips SET views = views + 1 WHERE id = ?", (clip_id,))

def get_user_clips(username: str, limit: int = 50) -> list:
    """Get all clips for a user"""
    with get_db() as conn:
        cursor = conn.cursor()
        cursor.execute(
            "SELECT * FROM clips WHERE owner = ? ORDER BY created_at DESC LIMIT ?",
            (username, limit)
        )
        rows = cursor.fetchall()
        return [dict(row) for row in rows]