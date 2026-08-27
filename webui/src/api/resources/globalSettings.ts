import type { JsonObject, GlobalSettings } from '../types';
import { readResource, writeResource } from './helpers';

export const globalSettingsPath = '/v1/global-settings';
export type GlobalSettingsPatch = JsonObject | Partial<GlobalSettings>;

export function getGlobalSettings(): Promise<GlobalSettings> {
  return readResource<GlobalSettings>(globalSettingsPath);
}

export function patchGlobalSettings(body: GlobalSettingsPatch, ifMatch: string): Promise<GlobalSettings> {
  return writeResource<GlobalSettings>(
    'PATCH',
    globalSettingsPath,
    ifMatch,
    body,
    'application/merge-patch+json',
  );
}

export const get = getGlobalSettings;
export const patch = patchGlobalSettings;
