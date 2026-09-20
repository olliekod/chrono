// 64 symbols, so masking a random byte with 63 is unbiased.
const ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";
const ID_LENGTH = 12;

/** Unguessable, URL-safe id (72 bits of randomness). */
export function newClipId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(ID_LENGTH));
  let id = "";
  for (const byte of bytes) id += ALPHABET[byte & 63];
  return id;
}

export function isClipId(value: string): boolean {
  return /^[A-Za-z0-9_-]{12}$/.test(value);
}
