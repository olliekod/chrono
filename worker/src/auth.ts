async function sha256(text: string): Promise<ArrayBuffer> {
  return crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
}

/**
 * Checks `Authorization: Bearer <key>` against the configured upload key.
 * Both sides are hashed first so the comparison is constant-time and doesn't leak the key's length.
 * Fails closed when no key is configured.
 */
export async function isAuthorized(request: Request, expectedKey: string | undefined): Promise<boolean> {
  if (!expectedKey) return false;

  const match = /^Bearer (.+)$/.exec(request.headers.get("Authorization") ?? "");
  if (!match) return false;

  const [given, expected] = await Promise.all([sha256(match[1]), sha256(expectedKey)]);
  return crypto.subtle.timingSafeEqual(given, expected);
}
