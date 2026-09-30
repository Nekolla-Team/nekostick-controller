import { ApiClientError, request } from '../client';
import type { ControllerState, ControllerTelemetry } from '../types';
import { requireData, rememberVersion } from './helpers';

export const statePath = '/v1/controller/state';
export const telemetryPath = '/v1/controller/telemetry';
const reloadSettingsPath = '/v1/controller/reload-settings';

export async function getState(): Promise<ControllerState> {
  const response = await request<ControllerState>('GET', statePath);
  return requireData(response);
}

export async function getTelemetry(): Promise<ControllerTelemetry> {
  const response = await request<ControllerTelemetry>('GET', telemetryPath);
  return requireData(response);
}

export async function reloadSettings(ifMatch: string): Promise<ControllerState> {
  const response = await request<ControllerState>('POST', reloadSettingsPath, { ifMatch });
  rememberVersion(reloadSettingsPath, response);
  return requireData(response);
}

/**
 * Reloading recycles the very transport carrying the reload request, so the response can
 * be dropped (network/unavailable) or mangled into an empty body (transport). Either way the
 * reload likely applied; callers should re-probe the new endpoint instead of failing.
 */
export function isReloadDrop(error: unknown): boolean {
  return (
    error instanceof ApiClientError &&
    (error.kind === 'network' || error.kind === 'unavailable' || error.kind === 'transport')
  );
}

export const getControllerState = getState;
