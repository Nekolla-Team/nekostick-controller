import { beforeEach, describe, expect, it } from 'vitest';
import { getVersion, invalidate, setVersion } from '../src/api/etag';

describe('ETag cache', () => {
  beforeEach(() => {
    invalidate();
  });

  it('stores and reads quoted strong ETags by logical path', () => {
    setVersion('/v1/routes', '"42"');
    expect(getVersion('/v1/routes')).toBe('"42"');
    expect(getVersion('/v1/services')).toBeUndefined();
  });

  it('invalidates one path or the complete cache', () => {
    setVersion('/v1/routes', '"1"');
    setVersion('/v1/services', '"2"');

    invalidate('/v1/routes');
    expect(getVersion('/v1/routes')).toBeUndefined();
    expect(getVersion('/v1/services')).toBe('"2"');

    invalidate();
    expect(getVersion('/v1/services')).toBeUndefined();
  });
});
