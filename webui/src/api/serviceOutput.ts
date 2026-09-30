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
