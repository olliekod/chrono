export interface ByteRange {
  offset: number;
  length: number;
}

/**
 * Interprets a Range header against a resource of `size` bytes.
 *  - null:          no usable range; serve the whole thing (also used for multi-range requests, which we don't support)
 *  - "unsatisfiable": the range starts past the end; answer 416
 */
export function parseRange(header: string | null, size: number): ByteRange | "unsatisfiable" | null {
  if (!header) return null;

  const match = /^bytes=(\d*)-(\d*)$/.exec(header.trim());
  if (!match) return null;

  const [, startText, endText] = match;
  if (startText === "" && endText === "") return null;

  // "bytes=-N": the last N bytes
  if (startText === "") {
    const suffix = Number(endText);
    if (suffix === 0) return "unsatisfiable";
    const length = Math.min(suffix, size);
    return { offset: size - length, length };
  }

  const start = Number(startText);
  if (start >= size) return "unsatisfiable";

  // "bytes=N-" runs to the end; an end past the end is clamped.
  const end = endText === "" ? size - 1 : Math.min(Number(endText), size - 1);
  if (end < start) return null;

  return { offset: start, length: end - start + 1 };
}
