import boto3
from botocore.client import Config
from config import settings

# Initialize R2 client (S3-compatible)
s3_client = boto3.client(
    's3',
    endpoint_url=settings.r2_endpoint_url,
    aws_access_key_id=settings.r2_access_key_id,
    aws_secret_access_key=settings.r2_secret_access_key,
    config=Config(signature_version='s3v4'),
    region_name='auto'
)

def upload_fileobj(file_obj, object_key: str, content_type: str = "video/mp4") -> str:
    """
    Upload file object directly to R2
    
    Args:
        file_obj: File-like object
        object_key: S3 object key (path in bucket)
        content_type: MIME type
    
    Returns:
        Public URL of uploaded file
    """
    s3_client.upload_fileobj(
        file_obj,
        settings.r2_bucket_name,
        object_key,
        ExtraArgs={'ContentType': content_type}
    )
    
    return f"{settings.r2_public_url}/{object_key}"

def delete_file(object_key: str):
    """Delete file from R2 storage"""
    s3_client.delete_object(
        Bucket=settings.r2_bucket_name,
        Key=object_key
    )

def get_public_url(object_key: str) -> str:
    """Get public URL for an object"""
    return f"{settings.r2_public_url}/{object_key}"