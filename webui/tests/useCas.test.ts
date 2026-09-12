import { beforeEach, describe, expect, it, vi } from 'vitest';
import { request } from '../src/api/client';
import { putSettings } from '../src/api/resources/extensions';
import { invalidate, getVersion } from '../src/api/etag';
import { buildMergePatch, useCas } from '../src/composables/useCas';
import { saveConnection } from '../src/stores/connection';

function envelope(data: unknown, version: number | null): string {
  return JSON.stringify({
    apiVersion: 1,
    ok: true,
    code: 'ok',
    message: 'The operation completed.',
    data,
    version,
  });
}

describe('buildMergePatch', () => {
  it('recurses through nested objects, deletes missing keys, and replaces arrays', () => {
    expect(buildMergePatch(
      { nested: { unchanged: true, removed: 'value', changed: 1 }, values: [1, 2] },
      { nested: { unchanged: true, changed: 2 }, values: [1, 2, 3] },
    )).toEqual({
      nested: { removed: null, changed: 2 },
      values: [1, 2, 3],
    });
  });

  it('uses null for explicit deletion and omits unchanged fields', () => {
    expect(buildMergePatch(
      { keep: 'same', remove: 'present' },
      { keep: 'same', remove: null },
    )).toEqual({ remove: null });
  });
  it('omits unchanged nested objects', () => {
    expect(buildMergePatch(
      { nested: { value: 1 } },
      { nested: { value: 1 } },
    )).toEqual({});
  });
});

describe('useCas', () => {
  beforeEach(() => {
    invalidate();
    saveConnection('http://127.0.0.1:48123', 'test-key');
  });

  it('reads the latest ETag, writes with If-Match, and invalidates on conflict', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(envelope({ enabled: true }, 7), {
        status: 200,
        headers: { ETag: '"7"' },
      }))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        apiVersion: 1,
        ok: false,
        code: 'precondition_failed',
        message: 'The configuration changed.',
        data: null,
        version: null,
      }), { status: 412 }));
    vi.stubGlobal('fetch', fetchMock);
    const queryClient = { invalidateQueries: vi.fn().mockResolvedValue(undefined) };
    const { run } = useCas(queryClient);

    const operation = run('/v1/routes/route-id', (ifMatch) => request(
      'PATCH',
      '/v1/routes/route-id',
      { ifMatch, contentType: 'application/merge-patch+json', body: { enabled: false } },
    ));

    await expect(operation).rejects.toMatchObject({ kind: 'conflict', status: 412 });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[1][1]).toMatchObject({
      headers: expect.objectContaining({ 'If-Match': '"7"' }),
    });
    expect(getVersion('/v1/routes/route-id')).toBeUndefined();
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: ['routes', 'route-id'],
    });
  });

  it('creates an absent settings document from the ETag of the 404 no_settings answer', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({
        apiVersion: 1,
        ok: false,
        code: 'no_settings',
        message: 'The extension settings document has not been created.',
        data: null,
        version: null,
      }), { status: 404, headers: { ETag: '"9"' } }))
      .mockResolvedValueOnce(new Response(envelope({
        extensionId: 'nekolla.sample',
        schemaVersion: 1,
        settings: { theme: 'dark' },
        version: 1,
      }, 10), { status: 200, headers: { ETag: '"10"' } }));
    vi.stubGlobal('fetch', fetchMock);
    const { run } = useCas({ invalidateQueries: vi.fn().mockResolvedValue(undefined) });

    await run('/v1/extensions/nekolla.sample/settings', (ifMatch) => putSettings(
      'nekolla.sample',
      { schemaVersion: 1, settings: { theme: 'dark' } },
      ifMatch,
    ));

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[1][1]).toMatchObject({
      headers: expect.objectContaining({ 'If-Match': '"9"' }),
    });
    expect(getVersion('/v1/extensions/nekolla.sample/settings')).toBe('"10"');
  });

  it('keeps a missing extension record fatal instead of creating it', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({
      apiVersion: 1,
      ok: false,
      code: 'not_found',
      message: 'The management resource was not found.',
      data: null,
      version: null,
    }), { status: 404, headers: { ETag: '"9"' } }));
    vi.stubGlobal('fetch', fetchMock);
    const { run } = useCas({ invalidateQueries: vi.fn().mockResolvedValue(undefined) });

    await expect(run('/v1/extensions/nekolla.ghost/settings', () => { throw new Error('unreachable'); }))
      .rejects.toMatchObject({ kind: 'not_found' });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
