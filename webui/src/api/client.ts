import { clearConnection, connection } from '../stores/connection';
import router from '@/router';
import type { Envelope } from './types';

export type ApiErrorKind =
  | 'unauthorized'
  | 'not_found'
  | 'transport_disabled'
  | 'reserved_route'
  | 'conflict'
  | 'precondition_required'
  | 'invalid_request'
  | 'method_not_allowed'
  | 'unsupported'
  | 'unavailable'
  | 'storage_unavailable'
  | 'response_too_large'
  | 'server'
  | 'network'
  | 'transport';

export interface RequestOptions {
  body?: unknown;
  contentType?: string;
  ifMatch?: string;
  signal?: AbortSignal;
}

export interface ApiResponse<T> {
  status: number;
  envelope: Envelope<T> | null;
  data: T | null;
  etag: string | undefined;
  version: number | null;
}

export interface ApiClientErrorDetails {
  status?: number;
  code?: string;
  kind: ApiErrorKind;
}

export class ApiClientError extends Error {
  readonly status: number | undefined;
  readonly code: string | undefined;
  readonly kind: ApiErrorKind;
  readonly details: ApiClientErrorDetails;

  constructor(message: string, details: ApiClientErrorDetails) {
    super(message);
    this.name = 'ApiClientError';
    this.status = details.status;
    this.code = details.code;
    this.kind = details.kind;
    this.details = details;
    Object.setPrototypeOf(this, new.target.prototype);
  }
}

const maxAttempts = 3;
const retryBaseDelayMs = 100;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isEnvelope(value: unknown): value is Envelope<unknown> {
  if (!isRecord(value)) {
    return false;
  }

  return (
    value.apiVersion === 1 &&
    typeof value.ok === 'boolean' &&
    typeof value.code === 'string' &&
    typeof value.message === 'string' &&
    'data' in value &&
    (value.version === null || typeof value.version === 'number')
  );
}

function normalizePath(path: string): string {
  const withoutTrailingSlashes = path.replace(/\/+$/, '');
  return withoutTrailingSlashes === '' ? '/' : withoutTrailingSlashes;
}

function normalizeLogicalPath(logicalPath: string): string {
  if (!logicalPath.startsWith('/')) {
    throw new ApiClientError('The logical API path must start with /.', {
      code: 'invalid_request',
      kind: 'invalid_request',
    });
  }
  if (logicalPath.includes('?') || logicalPath.includes('#')) {
    throw new ApiClientError('The logical API path must not contain a query or fragment.', {
      code: 'invalid_request',
      kind: 'invalid_request',
    });
  }
  return normalizePath(logicalPath);
}

/** Join a configured HostRoute prefix with a logical /v1/... path. */
export function joinHostRoute(baseUrl: string, logicalPath: string): string {
  const normalizedLogicalPath = normalizeLogicalPath(logicalPath);
  const fallbackOrigin = typeof window !== 'undefined' && window.location.origin !== ''
    ? window.location.origin
    : 'http://localhost';
  const parsedBase = new URL(baseUrl, fallbackOrigin);
  const prefix = normalizePath(parsedBase.pathname);
  const pathname = prefix === '/v1'
    ? normalizedLogicalPath
    : `${prefix === '/' ? '' : prefix}${normalizedLogicalPath}`;

  parsedBase.pathname = pathname;
  parsedBase.search = '';
  parsedBase.hash = '';
  return parsedBase.toString();
}

/** Derive the controller API base from the page's transport root. */
export function defaultControllerBaseUrl(): string | null {
  if (typeof window === 'undefined' || window.location.origin === '') return null
  if (window.location.pathname === '/') return window.location.origin
  const pathname = window.location.pathname.replace(/\/+$/, '')
  return `${window.location.origin}${pathname}`
}


function configuredBaseUrl(): string | null {
  const configured = connection.baseUrl?.trim()
  if (configured) {
    return configured
  }
  return defaultControllerBaseUrl() ?? (
    typeof window !== 'undefined' && window.location.origin !== ''
      ? window.location.origin
      : null
  )
}

function serializeBody(body: unknown): string | undefined {
  if (typeof body === 'string') {
    return body;
  }
  return JSON.stringify(body);
}

function getHeader(response: Response, name: string): string | undefined {
  return response.headers.get(name) ?? undefined;
}
function isAbortError(error: unknown): boolean {
  return isRecord(error) && error.name === 'AbortError';
}

function kindFor(status: number, code: string | undefined): ApiErrorKind {
  switch (status) {
    case 401:
      return 'unauthorized';
    case 405:
      return 'method_not_allowed';
    case 409:
      return 'reserved_route';
    case 412:
      return 'conflict';
    case 428:
      return 'precondition_required';
    default:
      break;
  }

  switch (code) {
    case 'unauthorized':
      return 'unauthorized';
    case 'transport_disabled':
      return 'transport_disabled';
    case 'not_found':
      return 'not_found';
    case 'reserved_route':
      return 'reserved_route';
    case 'precondition_failed':
      return 'conflict';
    case 'precondition_required':
      return 'precondition_required';
    case 'invalid_request':
      return 'invalid_request';
    case 'method_not_allowed':
      return 'method_not_allowed';
    case 'unsupported':
      return 'unsupported';
    case 'unavailable':
      return 'unavailable';
    case 'storage_unavailable':
      return 'storage_unavailable';
    case 'response_too_large':
      return 'response_too_large';
    default:
      break;
  }

  switch (status) {
    case 400:
      return 'invalid_request';
    case 404:
      return 'not_found';
    default:
      return status >= 500 ? 'server' : 'transport';
  }
}

function redirectToConnect(): void {
  if (router.currentRoute.value.path !== '/connect') {
    void router.push('/connect');
  }
}

function throwResponseError(status: number, envelope: Envelope<unknown>): never {
  const kind = kindFor(status, envelope.code);
  const error = new ApiClientError(envelope.message || envelope.code, {
    status,
    code: envelope.code,
    kind,
  });
  if (kind === 'unauthorized') {
    clearConnection();
    redirectToConnect();
  }
  throw error;
}
function throwMalformedResponse(status: number, detail: string): never {
  const kind: ApiErrorKind = status === 401 ? 'unauthorized' : 'transport';
  if (kind === 'unauthorized') {
    clearConnection();
    redirectToConnect();
  }
  throw new ApiClientError(detail, {
    status,
    kind,
  });
}

interface ParsedResponse {
  envelope: Envelope<unknown> | null;
  data: unknown | null;
  etag: string | undefined;
  version: number | null;
}

async function parseResponse(response: Response): Promise<ParsedResponse> {
  const etag = getHeader(response, 'ETag');
  const status = response.status;
  let body: string;
  try {
    body = await response.text();
  } catch {
    return throwMalformedResponse(status, 'The controller response could not be read.');
  }

  if (body.trim() === '') {
    if (status === 204) {
      return { envelope: null, data: null, etag, version: null };
    }
    return throwMalformedResponse(status, 'The controller response had an empty body.');
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(body) as unknown;
  } catch {
    return throwMalformedResponse(status, 'The controller response was not valid JSON.');
  }

  if (!isEnvelope(parsed)) {
    return throwMalformedResponse(status, 'The controller response was not a valid envelope.');
  }

  if (status < 200 || status >= 300 || !parsed.ok) {
    throwResponseError(status, parsed);
  }

  return {
    envelope: parsed,
    data: parsed.data,
    etag,
    version: parsed.version,
  };
}

function requestInit(method: string, options: RequestOptions): RequestInit {
  const headers: Record<string, string> = {};
  if (connection.apiKey !== null) {
    headers['x-nekostick-controller-key'] = connection.apiKey;
  }
  if (options.ifMatch !== undefined) {
    headers['If-Match'] = options.ifMatch;
  }

  const serializedBody = method === 'GET' || method === 'HEAD'
    ? undefined
    : options.body === undefined ? undefined : serializeBody(options.body);
  if (serializedBody !== undefined && method !== 'GET' && method !== 'HEAD') {
    headers['Content-Type'] = options.contentType ?? 'application/json';
  }

  const init: RequestInit = { method, headers, cache: 'no-store' };
  if (options.signal !== undefined) {
    init.signal = options.signal;
  }
  if (serializedBody !== undefined && method !== 'GET' && method !== 'HEAD') {
    init.body = serializedBody;
  }
  return init;
}

function retryDelay(attempt: number): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, retryBaseDelayMs * 2 ** attempt);
  });
}

function networkError(error: unknown): ApiClientError {
  const message = error instanceof Error && error.message !== ''
    ? error.message
    : 'The controller request could not be sent.';
  return new ApiClientError(message, { kind: 'network' });
}

/** Send one management API request with envelope parsing and bounded retries. */
export async function request<T = unknown>(
  method: string,
  logicalPath: string,
  options: RequestOptions = {},
): Promise<ApiResponse<T>> {
  const baseUrl = configuredBaseUrl();
  if (baseUrl === null) {
    throw new ApiClientError('No controller connection is configured.', { kind: 'network' });
  }
  let url: string;
  try {
    url = joinHostRoute(baseUrl, logicalPath);
  } catch (error: unknown) {
    if (error instanceof ApiClientError) {
      throw error;
    }
    throw new ApiClientError('The controller base URL is invalid.', { kind: 'network' });
  }
  const normalizedMethod = method.toUpperCase();
  const init = requestInit(normalizedMethod, options);
  const canRetryNetwork = normalizedMethod === 'GET' || normalizedMethod === 'HEAD';

  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    let response: Response;
    try {
      response = await fetch(url, init);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        throw new ApiClientError('The controller request was cancelled.', { kind: 'transport' });
      }
      if (error instanceof ApiClientError && error.kind !== 'network') {
        throw error;
      }
      if (canRetryNetwork && attempt < maxAttempts - 1) {
        await retryDelay(attempt);
        continue;
      }
      throw error instanceof ApiClientError ? error : networkError(error);
    }

    try {
      const parsed = await parseResponse(response);
      if (response.status === 503 && attempt < maxAttempts - 1) {
        await retryDelay(attempt);
        continue;
      }
      return {
        status: response.status,
        envelope: parsed.envelope as Envelope<T> | null,
        data: parsed.data as T | null,
        etag: parsed.etag,
        version: parsed.version,
      };
    } catch (error: unknown) {
      if (response.status === 503 && attempt < maxAttempts - 1) {
        await retryDelay(attempt);
        continue;
      }
      throw error;
    }
  }

  throw new ApiClientError('The controller request could not be completed.', { kind: 'network' });
}
