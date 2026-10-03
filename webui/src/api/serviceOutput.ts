import { connection } from '../stores/connection';
import { defaultControllerBaseUrl, joinHostRoute } from './client';
import type { ServiceLogEntry } from './types';

export type ServiceOutputStreamKind = 'stdout' | 'stderr';

export type ServiceOutputCloseKind =
  | 'processExited'
  | 'sessionEnded'
  | 'fault'
  | 'abnormal'
  | 'unknown';

/** Subprotocol token prefix carrying the base64url-encoded API key; mirrors the server contract. */
export const keySubProtocolPrefix = 'nekostick.controller.key.';

/**
 * Browsers cannot set request headers on a WebSocket upgrade, so the API key travels as one
 * base64url-encoded Sec-WebSocket-Protocol token (the server never echoes a subprotocol).
 */
export function encodeKeySubProtocol(apiKey: string): string {
  const bytes = new TextEncoder().encode(apiKey);
  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  const base64url = btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return keySubProtocolPrefix + base64url;
}

/** Resolve the WebSocket URL for one service's live output against the active connection. */
export function buildServiceOutputUrl(
  serviceId: string,
  stream: ServiceOutputStreamKind,
): string | null {
  const configured = connection.baseUrl?.trim();
  const base = configured || defaultControllerBaseUrl();
  if (!base) return null;

  const url = new URL(joinHostRoute(base, `/v1/services/${serviceId}/output/stream`));
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  url.searchParams.set('stream', stream);
  return url.toString();
}

/** Resolve the SSE URL for one service's live output against the active connection. */
export function buildServiceOutputEventStreamUrl(
  serviceId: string,
  stream: ServiceOutputStreamKind,
  sinceSequence?: number | null,
): string | null {
  const configured = connection.baseUrl?.trim();
  const base = configured || defaultControllerBaseUrl();
  if (!base) return null;

  const url = new URL(joinHostRoute(base, `/v1/services/${serviceId}/output/stream`));
  url.searchParams.set('stream', stream);
  if (sinceSequence !== undefined && sinceSequence !== null) {
    url.searchParams.set('since', String(sinceSequence));
  }
  return url.toString();
}

export type ServiceOutputEndReason =
  | 'processExited'
  | 'sessionEnded'
  | 'fault'
  | 'extensionUnloaded'
  | 'serviceDisabled'
  | 'serviceRemoved'
  | 'hostShutdown';

export type ServiceOutputEventStreamFailure =
  | { kind: 'http'; status: number; code?: string }
  | { kind: 'network' }
  | { kind: 'truncated' };

export interface ServiceOutputEventStreamHandlers {
  onOpen(): void;
  onChunk(bytes: Uint8Array, sequence?: number): void;
  onState(entry: ServiceLogEntry, sequence?: number): void;
  onEnd(reason: ServiceOutputEndReason): void;
  onFailure(failure: ServiceOutputEventStreamFailure): void;
}

export interface ServiceOutputEventStreamHandle {
  close(): void;
  readonly done: Promise<void>;
}

export function openServiceOutputEventStream(
  url: string,
  apiKey: string,
  handlers: ServiceOutputEventStreamHandlers,
): ServiceOutputEventStreamHandle {
  const abort = new AbortController();
  const done = (async () => {
    let response: Response;
    try {
      response = await fetch(url, {
        headers: { Accept: 'text/event-stream', 'x-nekostick-controller-key': apiKey },
        signal: abort.signal,
      });
    } catch {
      if (!abort.signal.aborted) handlers.onFailure({ kind: 'network' });
      return;
    }

    if (!response.ok || !response.body) {
      handlers.onFailure({
        kind: 'http',
        status: response.status,
        code: response.ok ? undefined : await readServiceOutputErrorCode(response),
      });
      return;
    }

    handlers.onOpen();
    const reader = response.body.getReader();
    const utf8 = new TextDecoder();
    let pending = '';
    let ended = false;

    const dispatchFrame = (frame: string): void => {
      let eventName = '';
      let sequence: number | undefined;
      const data: string[] = [];
      for (const rawLine of frame.split('\n')) {
        const line = rawLine.endsWith('\r') ? rawLine.slice(0, -1) : rawLine;
        if (line.startsWith(':')) continue;
        if (line.startsWith('event:')) {
          eventName = line.slice(6).trim();
          continue;
        }
        if (line.startsWith('id:')) {
          const value = line.slice(3).trim();
          const parsed = Number(value);
          if (value !== '' && Number.isFinite(parsed)) sequence = parsed;
          continue;
        }
        if (!line.startsWith('data:')) continue;
        const value = line.slice(5);
        data.push(value.startsWith(' ') ? value.slice(1) : value);
      }

      if (eventName === 'end') {
        ended = true;
        handlers.onEnd(parseServiceOutputEndReason(data.join('\n')));
        return;
      }
      if (data.length === 0) return;

      const payload = data.join('\n');
      if (eventName === 'state') {
        try {
          const entry = JSON.parse(payload) as ServiceLogEntry;
          handlers.onState(entry, sequence ?? entry.sequence ?? undefined);
        } catch {
          // Ignore malformed state frames; output data and the rest of the stream remain usable.
        }
        return;
      }

      handlers.onChunk(decodeServiceOutputChunk(payload), sequence);
    };

    try {
      while (true) {
        const { value, done: streamDone } = await reader.read();
        if (streamDone) break;
        pending += utf8.decode(value, { stream: true });
        let separator = pending.indexOf('\n\n');
        while (separator >= 0) {
          dispatchFrame(pending.slice(0, separator));
          pending = pending.slice(separator + 2);
          separator = pending.indexOf('\n\n');
        }
        if (ended) break;
      }
    } catch {
      // Aborted by close() or a transport drop; the tail below classifies it.
    }

    if (!ended && !abort.signal.aborted) handlers.onFailure({ kind: 'truncated' });
  })();

  return {
    close: () => abort.abort(),
    done,
  };
}

/**
 * Best-effort extraction of the controller error envelope's `code` from a failed response;
 * transport-level errors (proxy pages, empty 400s) carry no envelope and yield undefined.
 */
async function readServiceOutputErrorCode(response: Response): Promise<string | undefined> {
  try {
    const body: unknown = await response.json();
    if (body !== null && typeof body === 'object' && 'code' in body && typeof body.code === 'string') {
      return body.code;
    }
  } catch {
    // Not an envelope body; nothing more to report.
  }
  return undefined;
}

/** Decode one base64 SSE data frame into raw output bytes. */
export function decodeServiceOutputChunk(data: string): Uint8Array {
  const binary = atob(data);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

/** Parse the terminal end frame's reason, defaulting to fault for unknown payloads. */
export function parseServiceOutputEndReason(data: string): ServiceOutputEndReason {
  try {
    const parsed: unknown = JSON.parse(data);
    if (parsed && typeof parsed === 'object' && 'reason' in parsed) {
      const reason: unknown = parsed.reason;
      if (
        reason === 'processExited'
        || reason === 'sessionEnded'
        || reason === 'fault'
        || reason === 'extensionUnloaded'
        || reason === 'serviceDisabled'
        || reason === 'serviceRemoved'
        || reason === 'hostShutdown'
      ) {
        return reason;
      }
    }
  } catch {
    // fall through to fault
  }
  return 'fault';
}

/** Map the endpoint's documented close codes onto displayable reason keys. */
export function classifyCloseCode(code: number): ServiceOutputCloseKind {
  switch (code) {
    case 1000:
      return 'processExited';
    case 1001:
      return 'sessionEnded';
    case 1011:
      return 'fault';
    case 1006:
      return 'abnormal';
    default:
      return 'unknown';
  }
}
