import type {
  ExtensionRecord,
  ExtensionSettings,
  ExtensionSettingsWriteBody,
} from '../types';
import { readResource, writeResource, deleteResource } from './helpers';

export const extensionsPath = '/v1/extensions';

function extensionPath(id: string): string {
  return `${extensionsPath}/${encodeURIComponent(id)}`;
}

function settingsPath(id: string): string {
  return `${extensionPath(id)}/settings`;
}

export function listExtensions(): Promise<ExtensionRecord[]> {
  return readResource<ExtensionRecord[]>(extensionsPath);
}

export function getExtension(id: string): Promise<ExtensionRecord> {
  return readResource<ExtensionRecord>(extensionPath(id));
}

export function getSettings(id: string): Promise<ExtensionSettings> {
  return readResource<ExtensionSettings>(settingsPath(id));
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

export const list = listExtensions;
export const get = getExtension;
export const put = putSettings;
export const removeSettings = deleteSettings;
export { deleteSettings as delete };
