-- Proof that a request comes from the app that uploaded a clip. The uploader is given a random token once, when the
-- upload starts; only its SHA-256 hash is stored here. Renaming or removing a clip needs the upload key AND that clip's
-- token, so someone else who has the shared key can't touch it. Clips uploaded before this have no hash: they can still be
-- renamed with the key alone, and cannot be removed through the API.
ALTER TABLE clips ADD COLUMN owner_token_hash TEXT;
