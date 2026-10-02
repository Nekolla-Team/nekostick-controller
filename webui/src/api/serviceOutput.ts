import { connection } from '../stores/connection';
import { defaultControllerBaseUrl, joinHostRoute } from './client';

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
): string | null {
  const configured = connection.baseUrl?.trim();
  const base = configured || defaultControllerBaseUrl();
  if (!base) return null;

  const url = new URL(joinHostRoute(base, `/v1/services/${serviceId}/output/stream`));
  url.searchParams.set('stream', stream);
  return url.toString();
}

export type ServiceOutputEndReason = 'processExited' | 'sessionEnded' | 'fault';

export type ServiceOutputEventStreamFailure =
  | { kind: 'http'; status: number }
  | { kind: 'network' }
  | { kind: 'truncated' };

export interface ServiceOutputEventStreamHandlers {
  onOpen(): void;
  onChunk(bytes: Uint8Array): void;
  onEnd(reason: ServiceOutputEndReason): void;
  onFailure(failure: ServiceOutputEventStreamFailure): void;
}

export interface ServiceOutputEventStreamHandle {
  close(): void;
  readonly done: Promise<void>;
}

/**
 * Opens the service-output SSE stream with fetch so the API key travels in the request header;
 * native EventSource cannot set headers. HostRoute cannot carry a WebSocket upgrade, so this is
 * the only output transport available there.
 */
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
      handlers.onFailure({ kind: 'http', status: response.status });
      return;
    }

    handlers.onOpen();
    const reader = response.body.getReader();
    const utf8 = new TextDecoder();
    let pending = '';
    let eventName = '';
    let ended = false;

    const dispatchFrame = (frame: string): void => {
      for (const line of frame.split('\n')) {
        if (line.startsWith(':')) continue;
        if (line.startsWith('event:')) {
          eventName = line.slice(6).trim();
          continue;
        }
        if (!line.startsWith('data:')) continue;
        const data = line.slice(5);
        if (eventName === 'end') {
          ended = true;
          handlers.onEnd(parseServiceOutputEndReason(data));
        } else {
          handlers.onChunk(decodeServiceOutputChunk(data));
        }
      }
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
      if (reason === 'processExited' || reason === 'sessionEnded' || reason === 'fault') {
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
