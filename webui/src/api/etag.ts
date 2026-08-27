const versions = new Map<string, string>();

/** Return the last strong ETag observed for a logical API path. */
export function getVersion(path: string): string | undefined {
  return versions.get(path);
}

/** Store an ETag exactly as returned by the controller. */
export function setVersion(path: string, etag: string): void {
  versions.set(path, etag);
}

/** Invalidate one path, or the complete cache when no path is supplied. */
export function invalidate(path?: string): void {
  if (path === undefined) {
    versions.clear();
    return;
  }

  versions.delete(path);
}
