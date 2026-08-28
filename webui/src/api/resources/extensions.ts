import type {
  ExtensionRecord,
  ExtensionRefreshSummary,
  ExtensionSettings,
  ExtensionSettingsWriteBody,
} from '../types';
import { actionResource, actionResourceData, readResource, writeResource, deleteResource } from './helpers';

export const extensionsPath = '/v1/extensions';

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
