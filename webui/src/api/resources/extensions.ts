import type {
  Envelope,
  ExtensionInstallResult,
  ExtensionRecord,
  ExtensionRefreshSummary,
  ExtensionSettings,
  ExtensionSettingsWriteBody,
} from '../types';
import { ApiClientError, defaultControllerBaseUrl, joinHostRoute, type ApiErrorKind } from '../client';
import { connection } from '../../stores/connection';
import { actionResource, actionResourceData, readResource, writeResource, deleteResource } from './helpers';

export const extensionsPath = '/v1/extensions';

let activeExtensionUpload: XMLHttpRequest | null = null;

function isEnvelope(value: unknown): value is Envelope<unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false;
  const envelope = value as Record<string, unknown>;
  return (
    envelope.apiVersion === 1 &&
    typeof envelope.ok === 'boolean' &&
    typeof envelope.code === 'string' &&
    typeof envelope.message === 'string' &&
    'data' in envelope &&
    (envelope.version === null || typeof envelope.version === 'number')
  );
}

function isExtensionInstallResult(value: unknown): value is ExtensionInstallResult {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false;
  const result = value as Record<string, unknown>;
  return typeof result.id === 'string' && typeof result.version === 'string' && typeof result.replaced === 'boolean';
}

function uploadErrorKind(status: number, code: string | undefined): ApiErrorKind {
  if (status === 401 || code === 'unauthorized') return 'unauthorized';
  if (status === 400 || code === 'invalid_request') return 'invalid_request';
  if (status === 409 || code === 'downgrade_forbidden') return 'conflict';
  if (status === 501 || code === 'unsupported') return 'unsupported';
  if (status === 503 || code === 'storage_unavailable') return 'storage_unavailable';
  return status >= 500 ? 'server' : 'transport';
}

function uploadNetworkError(message = 'The extension package upload could not be sent.'): ApiClientError {
  return new ApiClientError(message, { kind: 'network' });
}

function parseInstallResponse(upload: XMLHttpRequest): ExtensionInstallResult {
  const status = upload.status;
  let parsed: unknown;
  try {
    parsed = JSON.parse(upload.responseText) as unknown;
  } catch {
    throw new ApiClientError('The controller response was not valid JSON.', {
      status,
      kind: uploadErrorKind(status, undefined),
    });
  }

  if (!isEnvelope(parsed)) {
    throw new ApiClientError('The controller response was not a valid envelope.', {
      status,
      kind: uploadErrorKind(status, undefined),
    });
  }

  if (status < 200 || status >= 300 || !parsed.ok) {
    throw new ApiClientError(parsed.message || parsed.code, {
      status,
      code: parsed.code,
      kind: uploadErrorKind(status, parsed.code),
    });
  }

  if (!isExtensionInstallResult(parsed.data)) {
    throw new ApiClientError('The controller response did not contain the expected extension installation result.', {
      status,
      kind: 'transport',
    });
  }
  return parsed.data;
}

export function cancelExtensionPackageUpload(): void {
  activeExtensionUpload?.abort();
}

export function installExtensionPackage(
  file: File,
  onProgress: (fraction: number) => void,
): Promise<ExtensionInstallResult> {
  const baseUrl = connection.baseUrl?.trim() || defaultControllerBaseUrl();
  if (baseUrl === null) {
    return Promise.reject(new ApiClientError('No controller connection is configured.', { kind: 'network' }));
  }

  let url: string;
  try {
    url = joinHostRoute(baseUrl, `${extensionsPath}/install`);
  } catch (error: unknown) {
    return Promise.reject(error instanceof ApiClientError ? error : uploadNetworkError('The controller base URL is invalid.'));
  }

  if (typeof XMLHttpRequest === 'undefined') {
    return Promise.reject(uploadNetworkError('The current transport cannot upload extension packages.'));
  }

  return new Promise<ExtensionInstallResult>((resolve, reject) => {
    const upload = new XMLHttpRequest();
    let settled = false;
    const finish = (callback: () => void): void => {
      if (settled) return;
      settled = true;
      if (activeExtensionUpload === upload) activeExtensionUpload = null;
      callback();
    };

    activeExtensionUpload = upload;
    upload.upload.onprogress = (event) => {
      if (event.lengthComputable && event.total > 0) {
        onProgress(Math.min(1, event.loaded / event.total));
      } else {
        onProgress(Number.NaN);
      }
    };
    upload.onload = () => {
      try {
        const result = parseInstallResponse(upload);
        finish(() => resolve(result));
      } catch (error: unknown) {
        finish(() => reject(error));
      }
    };
    upload.onerror = () => finish(() => reject(uploadNetworkError()));
    upload.onabort = () => finish(() => reject(uploadNetworkError('The extension package upload was cancelled.')));

    try {
      upload.open('POST', url);
      upload.setRequestHeader('Content-Type', 'application/zip');
      if (connection.apiKey !== null) {
        upload.setRequestHeader('x-nekostick-controller-key', connection.apiKey);
      }
      upload.send(file);
    } catch (error: unknown) {
      finish(() => reject(error instanceof Error ? uploadNetworkError(error.message) : uploadNetworkError()));
    }
  });
}

function extensionPath(id: string): string {
  return `${extensionsPath}/${encodeURIComponent(id)}`;
}

function settingsPath(id: string): string {
  return `${extensionPath(id)}/settings`;
}

export const extensionSettingsPath = settingsPath;

export function listExtensions(): Promise<ExtensionRecord[]> {
  return readResource<ExtensionRecord[]>(extensionsPath);
}

export function getExtension(id: string): Promise<ExtensionRecord> {
  return readResource<ExtensionRecord>(extensionPath(id));
}

export async function getSettings(id: string): Promise<ExtensionSettings> {
  try {
    return await readResource<ExtensionSettings>(settingsPath(id));
  } catch (error: unknown) {
    // `no_settings` means the record exists without a persisted document, which clients treat
    // as an empty document they may create with the conditional PUT.
    if (error instanceof ApiClientError && error.kind === 'no_settings') {
      return { extensionId: id, schemaVersion: 1, settings: {}, version: 0 };
    }
    throw error;
  }
}

export function putSettings(
  id: string,
  body: ExtensionSettingsWriteBody,
  ifMatch: string,
): Promise<ExtensionSettings> {
  return writeResource<ExtensionSettings>('PUT', settingsPath(id), ifMatch, body, 'application/json');
}

export function deleteSettings(id: string, ifMatch: string): Promise<void> {
  return deleteResource(settingsPath(id), ifMatch);
}
export function enableExtension(id: string): Promise<void> {
  return actionResource(`${extensionPath(id)}/enable`);
}

export function disableExtension(id: string): Promise<void> {
  return actionResource(`${extensionPath(id)}/disable`);
}

export function reloadExtension(id: string): Promise<void> {
  return actionResource(`${extensionPath(id)}/reload`);
}

export function deleteExtensionRecord(id: string): Promise<void> {
  return actionResource(`${extensionPath(id)}/record`, 'DELETE');
}

export function refreshExtensions(): Promise<ExtensionRefreshSummary> {
  return actionResourceData<ExtensionRefreshSummary>(`${extensionsPath}/refresh`);
}

export const list = listExtensions;
export const get = getExtension;
export const put = putSettings;
export const removeSettings = deleteSettings;
export { deleteSettings as delete };
