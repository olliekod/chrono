/** Header the app sends with the owner token of the clip it wants to rename or remove. */
export const OWNER_TOKEN_HEADER = "X-Owner-Token";

// 32 random bytes as URL-safe base64 with no padding: 43 characters, 256 bits.
const TOKEN_PATTERN = /^[A-Za-z0-9_-]{43}$/;

function toBase64Url(bytes: Uint8Array): string {
  let text = "";
  for (const byte of bytes) text += String.fromCharCode(byte);
  return btoa(text).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** A secret only the uploading app is given. Unguessable; it is never stored, only its hash. */
export function newOwnerToken(): string {
  return toBase64Url(crypto.getRandomValues(new Uint8Array(32)));
}

/** Lowercase hex SHA-256, the form stored in the database. */
export async function hashOwnerToken(token: string): Promise<string> {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(token)));
  return Array.from(digest, (b) => b.toString(16).padStart(2, "0")).join("");
}

/**
 * Does this request carry the token that goes with `storedHash`? Both sides are hashes of fixed length, compared in
 * constant time, so the comparison doesn't reveal how much of a guess was right. A missing or malformed token is simply no.
 */
export async function carriesOwnerToken(request: Request, storedHash: string | null): Promise<boolean> {
  if (!storedHash) return false;

  const given = request.headers.get(OWNER_TOKEN_HEADER) ?? "";
  // Still hash something when the header is missing or malformed, so every request takes about the same work.
  const givenHash = await hashOwnerToken(TOKEN_PATTERN.test(given) ? given : "");
  if (!TOKEN_PATTERN.test(given)) return false;

  const encode = (text: string) => new TextEncoder().encode(text);
  const a = encode(givenHash);
  const b = encode(storedHash);
  return a.byteLength === b.byteLength && crypto.subtle.timingSafeEqual(a, b);
}
