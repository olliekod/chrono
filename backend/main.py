from fastapi import FastAPI, UploadFile, File, Form, HTTPException, Header
from fastapi.responses import HTMLResponse
from fastapi.middleware.cors import CORSMiddleware
from datetime import datetime, timedelta
from jose import JWTError, jwt
from nanoid import generate
import os

from config import settings
from database import init_db, insert_clip, get_clip, increment_views
from storage import upload_fileobj, get_public_url

app = FastAPI(title="Game Clip Recorder API")

# CORS - allow requests from anywhere (restrict in production)
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

@app.on_event("startup")
async def startup():
    init_db()
    print("✓ Server started successfully")

# ===== AUTH =====

def create_upload_token(username: str) -> str:
    """Create short-lived JWT for uploads"""
    expire = datetime.utcnow() + timedelta(minutes=settings.upload_token_expire_minutes)
    to_encode = {"sub": username, "exp": expire, "type": "upload"}
    return jwt.encode(to_encode, settings.jwt_secret_key, algorithm=settings.jwt_algorithm)

def verify_upload_token(token: str) -> str:
    """Verify token and return username"""
    try:
        payload = jwt.decode(token, settings.jwt_secret_key, algorithms=[settings.jwt_algorithm])
        username: str = payload.get("sub")
        if username is None or payload.get("type") != "upload":
            raise HTTPException(status_code=401, detail="Invalid token")
        return username
    except JWTError:
        raise HTTPException(status_code=401, detail="Invalid or expired token")

@app.post("/auth/token")
async def get_upload_token(username: str = Form(...)):
    """Get upload token - Usage: curl -X POST http://localhost:8000/auth/token -F "username=yourname" """
    token = create_upload_token(username)
    return {"token": token, "expires_in": settings.upload_token_expire_minutes * 60}

# ===== UPLOAD =====

@app.post("/upload")
async def upload_clip(
    file: UploadFile = File(...),
    username: str = Form(...),
    duration: float = Form(None),
    resolution: str = Form(None),
    fps: int = Form(None),
    bitrate: int = Form(None),
    authorization: str = Header(None)
):
    """
    Upload clip file
    Returns: {"link": "http://localhost:8000/watch/abc123", "clip_id": "abc123"}
    """
    
    # Optional: verify token if provided
    if authorization and authorization.startswith("Bearer "):
        token = authorization.replace("Bearer ", "")
        token_user = verify_upload_token(token)
        if token_user != username:
            raise HTTPException(status_code=403, detail="Token username mismatch")
    
    # Validate file extension
    file_ext = os.path.splitext(file.filename)[1].lower()
    if file_ext not in settings.allowed_video_extensions:
        raise HTTPException(status_code=400, detail=f"Invalid file type. Allowed: {settings.allowed_video_extensions}")
    
    # Generate unique clip ID (12 chars, URL-safe)
    clip_id = generate(size=12)
    
    # Storage path: clips/{username}/{clip_id}.mp4
    object_key = f"clips/{username}/{clip_id}{file_ext}"
    
    # Upload to R2
    try:
        public_url = upload_fileobj(file.file, object_key)
        print(f"✓ Uploaded: {public_url}")
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Upload failed: {str(e)}")
    
    # Save metadata to database
    clip_data = {
        "id": clip_id,
        "owner": username,
        "filename": file.filename,
        "storage_path": object_key,
        "duration": duration,
        "file_size": file.size if hasattr(file, 'size') else None,
        "resolution": resolution,
        "fps": fps,
        "bitrate": bitrate,
        "settings": {}
    }
    
    insert_clip(clip_data)
    
    # Return shareable link
    watch_url = f"{settings.base_url}/watch/{clip_id}"
    print(f"✓ Clip saved: {watch_url}")
    
    return {
        "link": watch_url,
        "clip_id": clip_id,
        "storage_url": public_url
    }

# ===== VIEWING =====

@app.get("/watch/{clip_id}", response_class=HTMLResponse)
async def watch_clip(clip_id: str):
    """Display video player with Discord embed support"""
    clip = get_clip(clip_id)
    if not clip:
        raise HTTPException(status_code=404, detail="Clip not found")
    
    increment_views(clip_id)
    video_url = get_public_url(clip["storage_path"])
    
    html = f"""<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>{clip['owner']}'s Clip</title>
    
    <!-- Discord/Twitter embed tags -->
    <meta property="og:type" content="video.other">
    <meta property="og:title" content="{clip['owner']}'s Game Clip">
    <meta property="og:description" content="{clip['filename']} • {clip.get('duration_seconds', 0):.1f}s • {clip.get('views', 0)} views">
    <meta property="og:url" content="{settings.base_url}/watch/{clip_id}">
    <meta property="og:video" content="{video_url}">
    <meta property="og:video:url" content="{video_url}">
    <meta property="og:video:secure_url" content="{video_url}">
    <meta property="og:video:type" content="video/mp4">
    <meta property="og:video:width" content="1920">
    <meta property="og:video:height" content="1080">
    <meta name="twitter:card" content="player">
    <meta name="twitter:player" content="{video_url}">
    <meta name="twitter:player:width" content="1920">
    <meta name="twitter:player:height" content="1080">
    
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{
            background: #0e0e10;
            color: #efeff1;
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            min-height: 100vh;
            padding: 20px;
        }}
        .container {{ max-width: 1400px; width: 100%; }}
        video {{
            width: 100%;
            background: #000;
            border-radius: 8px;
            box-shadow: 0 8px 32px rgba(0,0,0,0.5);
        }}
        .info {{
            margin-top: 20px;
            padding: 20px;
            background: #18181b;
            border-radius: 8px;
        }}
        .info h2 {{ margin: 0 0 10px 0; color: #fff; }}
        .stats {{ color: #adadb8; font-size: 14px; margin-bottom: 15px; }}
        .copy-btn {{
            padding: 12px 24px;
            background: #9147ff;
            color: white;
            border: none;
            border-radius: 6px;
            cursor: pointer;
            font-size: 14px;
            font-weight: 600;
            transition: background 0.2s;
        }}
        .copy-btn:hover {{ background: #772ce8; }}
        .copy-btn.copied {{ background: #00c853; }}
    </style>
</head>
<body>
    <div class="container">
        <video controls autoplay muted loop>
            <source src="{video_url}" type="video/mp4">
        </video>
        <div class="info">
            <h2>{clip['filename']}</h2>
            <div class="stats">
                By <strong>{clip['owner']}</strong> • 
                {clip.get('duration_seconds', 0):.1f}s • 
                {clip.get('resolution', 'Unknown')} @ {clip.get('fps', '?')}fps • 
                {clip.get('views', 0)} views
            </div>
            <button class="copy-btn" onclick="copyLink()">📋 Copy Link</button>
        </div>
    </div>
    <script>
        function copyLink() {{
            navigator.clipboard.writeText(window.location.href).then(() => {{
                const btn = document.querySelector('.copy-btn');
                btn.textContent = '✓ Copied!';
                btn.classList.add('copied');
                setTimeout(() => {{
                    btn.textContent = '📋 Copy Link';
                    btn.classList.remove('copied');
                }}, 2000);
            }});
        }}
    </script>
</body>
</html>"""
    
    return HTMLResponse(content=html)

@app.get("/api/clip/{clip_id}")
async def get_clip_metadata(clip_id: str):
    """Get clip metadata as JSON"""
    clip = get_clip(clip_id)
    if not clip:
        raise HTTPException(status_code=404, detail="Clip not found")
    return clip

@app.get("/")
async def root():
    return {
        "message": "Game Clip Recorder API", 
        "version": "1.0",
        "endpoints": {
            "auth": "POST /auth/token",
            "upload": "POST /upload",
            "watch": "GET /watch/{clip_id}",
            "metadata": "GET /api/clip/{clip_id}"
        }
    }