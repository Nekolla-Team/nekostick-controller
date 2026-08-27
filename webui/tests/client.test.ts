import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiClientError, joinHostRoute, request } from '../src/api/client';
import { connection, saveConnection } from '../src/stores/connection';

function envelope<T>(data: T, version: number | null = 1): string {
  return JSON.stringify({
    apiVersion: 1,
    ok: true,
    code: 'ok',
    message: 'The operation completed.',
    data,
    version,
  });
}

function errorEnvelope(code: string, message = 'The management request is invalid.'): string {
  return JSON.stringify({
    apiVersion: 1,
    ok: false,
    code,
    message,
    data: null,
    version: null,
  });
}

describe('controller API client', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    saveConnection('http://127.0.0.1:48123', 'test-key');
  });

  it('parses successful envelopes and returns ETag/version metadata', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(envelope({ ready: true }, 42), {
      status: 200,
      headers: { ETag: '"42"' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await request<{ ready: boolean }>('GET', '/v1');

    expect(result.status).toBe(200);
    expect(result.data).toEqual({ ready: true });
    expect(result.envelope?.code).toBe('ok');
    expect(result.etag).toBe('"42"');
    expect(result.version).toBe(42);
    expect(fetchMock).toHaveBeenCalledWith(
      'http://127.0.0.1:48123/v1',
      expect.objectContaining({ method: 'GET' }),
    );
    const init = fetchMock.mock.calls[0][1] as RequestInit;
    expect(init.body).toBeUndefined();
  });

  it('maps canonical error envelopes to structured errors', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(errorEnvelope('not_found'), {
      status: 404,
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = request('GET', '/v1/routes/missing');

    await expect(result).rejects.toMatchObject({
      status: 404,
      code: 'not_found',
      kind: 'not_found',
    });
    await expect(result).rejects.toBeInstanceOf(ApiClientError);
  });

  it('maps unsupported responses to the unsupported kind', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(errorEnvelope('unsupported'), {
      status: 501,
    }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(request('GET', '/v1/controller/state')).rejects.toMatchObject({
      status: 501,
      code: 'unsupported',
      kind: 'unsupported',
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it.each([
    ['unavailable', 'unavailable'],
    ['storage_unavailable', 'storage_unavailable'],
    ['response_too_large', 'response_too_large'],
  ] as const)('maps 503 %s responses to %s', async (code, kind) => {
    const fetchMock = vi.fn().mockImplementation(() => new Response(errorEnvelope(code), {
      status: 503,
    }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(request('GET', '/v1')).rejects.toMatchObject({
      status: 503,
      code,
      kind,
    });
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it('maps an empty admission response to a transport error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('', { status: 400 })));

    await expect(request('POST', '/v1/controller/reload-settings', { ifMatch: '"1"' }))
      .rejects.toMatchObject({ status: 400, kind: 'transport' });
  });

  it('clears the connection before throwing unauthorized', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(errorEnvelope('unauthorized'), {
      status: 401,
    })));

    await expect(request('GET', '/v1')).rejects.toMatchObject({ kind: 'unauthorized' });
    expect(connection.baseUrl).toBeNull();
    expect(connection.apiKey).toBeNull();
  });

  it('retries safe reads on network failures but does not retry mutations', async () => {
    const getFetchMock = vi.fn().mockRejectedValue(new TypeError('offline'));
    vi.stubGlobal('fetch', getFetchMock);

    await expect(request('GET', '/v1')).rejects.toMatchObject({ kind: 'network' });
    expect(getFetchMock).toHaveBeenCalledTimes(3);

    const postFetchMock = vi.fn().mockRejectedValue(new TypeError('offline'));
    vi.stubGlobal('fetch', postFetchMock);

    await expect(request('POST', '/v1/routes', { body: {} }))
      .rejects.toMatchObject({ kind: 'network' });
    expect(postFetchMock).toHaveBeenCalledTimes(1);
  });

  it('retries network failures and 503, but not ordinary API errors', async () => {
    const fetchMock = vi.fn()
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValueOnce(new Response(envelope({ ok: true }), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(request('GET', '/v1')).resolves.toMatchObject({ status: 200 });
    expect(fetchMock).toHaveBeenCalledTimes(2);

    fetchMock.mockReset();
    fetchMock.mockResolvedValue(new Response(errorEnvelope('not_found'), { status: 404 }));
    await expect(request('GET', '/v1/routes/missing')).rejects.toMatchObject({ kind: 'not_found' });
    expect(fetchMock).toHaveBeenCalledTimes(1);

    fetchMock.mockReset();
    fetchMock
      .mockResolvedValueOnce(new Response(errorEnvelope('unavailable'), { status: 503 }))
      .mockResolvedValueOnce(new Response(envelope({ recovered: true }), { status: 200 }));
    await expect(request('GET', '/v1')).resolves.toMatchObject({ status: 200 });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});

describe('HostRoute URL joining', () => {
  it.each([
    ['http://localhost', 'http://localhost/v1'],
    ['http://localhost/admin', 'http://localhost/admin/v1'],
    ['http://localhost/v1', 'http://localhost/v1'],
  ])('joins %s with /v1 as %s', (baseUrl, expected) => {
    expect(joinHostRoute(baseUrl, '/v1')).toBe(expected);
  });
});
