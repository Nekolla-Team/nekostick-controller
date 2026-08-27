import { request } from '../client';
import type { ControllerState } from '../types';
import { requireData, rememberVersion } from './helpers';

export const statePath = '/v1/controller/state';
const reloadSettingsPath = '/v1/controller/reload-settings';

export async function getState(): Promise<ControllerState> {
  const response = await request<ControllerState>('GET', statePath);
  return requireData(response);
}

export async function reloadSettings(ifMatch: string): Promise<ControllerState> {
  const response = await request<ControllerState>('POST', reloadSettingsPath, { ifMatch });
  rememberVersion(reloadSettingsPath, response);
  return requireData(response);
}

export const getControllerState = getState;
