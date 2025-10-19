from pydantic_settings import BaseSettings

class Settings(BaseSettings):
    # Database (SQLite)
    database_url: str = "sqlite:///./clips.db"
    
    # Cloudflare R2 (S3-compatible)
    r2_access_key_id: str
    r2_secret_access_key: str
    r2_endpoint_url: str
    r2_bucket_name: str = "game-clips"
    r2_public_url: str
    
    # Security
    jwt_secret_key: str
    jwt_algorithm: str = "HS256"
    upload_token_expire_minutes: int = 15
    
    # Upload limits
    max_upload_size_mb: int = 500
    allowed_video_extensions: set = {".mp4", ".mkv", ".webm"}
    
    # Base URL for shareable links
    base_url: str = "http://localhost:8000"
    
    class Config:
        env_file = ".env"

settings = Settings()