import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  buildServiceOutputEventStreamUrl,
  buildServiceOutputUrl,
  classifyCloseCode,
  decodeServiceOutputChunk,
  encodeKeySubProtocol,
  keySubProtocolPrefix,
  openServiceOutputEventStream,
  parseServiceOutputEndReason,
  type ServiceOutputEndReason,
  type ServiceOutputEventStreamFailure,
} from '../src/api/serviceOutput';
import { saveConnection } from '../src/stores/connection';

function decodeBase64Url(encoded: string): string {
  const base64 = encoded.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4);
  const binary = atob(padded);
  return new TextDecoder().decode(Uint8Array.from(binary, (char) => char.charCodeAt(0)));
}

describe('service output helpers', () => {
  beforeEach(() => {
    saveConnection('http://127.0.0.1:48123', 'test-key');
  });

  it('encodes the API key as a base64url subprotocol token', () => {
    const key = 'key-with-unicode-你好-+/%';
    const token = encodeKeySubProtocol(key);

    expect(token.startsWith(keySubProtocolPrefix)).toBe(true);
    const encoded = token.slice(keySubProtocolPrefix.length);
    expect(encoded).toMatch(/^[A-Za-z0-9\-_]+$/);
    expect(decodeBase64Url(encoded)).toBe(key);
  });

  it('builds a ws URL with the stream query against the configured base', () => {
    const url = buildServiceOutputUrl('01234567-89ab-7cde-8f01-23456789abcd', 'stderr');

    expect(url).toBe(
      'ws://127.0.0.1:48123/v1/services/01234567-89ab-7cde-8f01-23456789abcd/output/stream?stream=stderr',
    );
  });

  it('derives wss for https bases', () => {
    saveConnection('https://controller.example:8443', 'test-key');

    expect(buildServiceOutputUrl('id', 'stdout')).toBe(
      'wss://controller.example:8443/v1/services/id/output/stream?stream=stdout',
    );
  });

  it('joins a HostRoute prefix for mounted deployments', () => {
    saveConnection('http://127.0.0.1:9000/admin', 'test-key');

    const url = buildServiceOutputUrl('id', 'stdout');
    expect(new URL(url!).pathname).toBe('/admin/v1/services/id/output/stream');
  });

  it('classifies the documented close codes', () => {
    expect(classifyCloseCode(1000)).toBe('processExited');
    expect(classifyCloseCode(1001)).toBe('sessionEnded');
    expect(classifyCloseCode(1011)).toBe('fault');
    expect(classifyCloseCode(1006)).toBe('abnormal');
    expect(classifyCloseCode(1005)).toBe('unknown');
  });
});

describe('service output event stream', () => {
  beforeEach(() => {
    saveConnection('http://127.0.0.1:48123', 'test-key');
  });

  function sseResponse(frames: string): Response {
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new TextEncoder().encode(frames));
        controller.close();
      },
    });
    return new Response(stream, { status: 200 });
  }


  it('keeps the http scheme and stream query for the SSE URL', () => {
    expect(buildServiceOutputEventStreamUrl('id', 'stderr')).toBe(
      'http://127.0.0.1:48123/v1/services/id/output/stream?stream=stderr',
    );
  });

  it('decodes padded base64 data frames', () => {
    expect(decodeServiceOutputChunk(btoa('hello\n'))).toEqual(new TextEncoder().encode('hello\n'));
  });

  it('parses documented end reasons and rejects unknown payloads', () => {
    expect(parseServiceOutputEndReason('{"reason":"processExited"}')).toBe('processExited');
    expect(parseServiceOutputEndReason('{"reason":"sessionEnded"}')).toBe('sessionEnded');
    expect(parseServiceOutputEndReason('{"reason":"mystery"}')).toBe('fault');
    expect(parseServiceOutputEndReason('not json')).toBe('fault');
  });

  it('streams chunks and the terminal end event', async () => {
    const frames = `data:${btoa('one\n')}\n\ndata:${btoa('two\n')}\n\nevent:end\ndata:{"reason":"processExited"}\n\n`;
    vi.stubGlobal('fetch', vi.fn(async () => sseResponse(frames)));

    const chunks: string[] = [];
    let endReason: ServiceOutputEndReason | null = null;
    const handle = openServiceOutputEventStream('http://x/stream', 'key', {
      onOpen: () => undefined,
      onChunk: (bytes) => chunks.push(new TextDecoder().decode(bytes)),
      onEnd: (reason) => {
        endReason = reason;
      },
      onFailure: () => undefined,
    });
    await handle.done;

    expect(chunks).toEqual(['one\n', 'two\n']);
    expect(endReason).toBe('processExited');
    vi.unstubAllGlobals();
  });

  it('reports non-200 responses as http failures with the status', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('nope', { status: 409 })));

    let failure: ServiceOutputEventStreamFailure | null = null;
    const handle = openServiceOutputEventStream('http://x/stream', 'key', {
      onOpen: () => undefined,
      onChunk: () => undefined,
      onEnd: () => undefined,
      onFailure: (reason) => {
        failure = reason;
      },
    });
    await handle.done;

    expect(failure).toEqual({ kind: 'http', status: 409 });
    vi.unstubAllGlobals();
  });

  it('reports an EOF without an end event as truncated', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => sseResponse(`data:${btoa('partial')}\n\n`)));

    let failure: ServiceOutputEventStreamFailure | null = null;
    const handle = openServiceOutputEventStream('http://x/stream', 'key', {
      onOpen: () => undefined,
      onChunk: () => undefined,
      onEnd: () => undefined,
      onFailure: (reason) => {
        failure = reason;
      },
    });
    await handle.done;

    expect(failure).toEqual({ kind: 'truncated' });
    vi.unstubAllGlobals();
  });
});
