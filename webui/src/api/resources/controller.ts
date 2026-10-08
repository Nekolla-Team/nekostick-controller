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
  markReloadInFlight();
  const response = await request<ControllerState>('POST', reloadSettingsPath, { ifMatch });
  rememberVersion(reloadSettingsPath, response);
  return requireData(response);
}

/**
 * Reloading recycles the very transport carrying the reload request, so the response can
 * be dropped (network/unavailable) or mangled into an empty body (transport). Either way the
 * reload likely applied; callers should re-probe the new endpoint instead of failing.
 *
 * The reload recycles the transport carrying every other in-flight request too, so the
 * always-on pollers see the same drops; see {@link isReloadWindowError} for the poller-side
 * counterpart.
 */
export function isReloadDrop(error: unknown): boolean {
  return (
    error instanceof ApiClientError &&
    (error.kind === 'network' || error.kind === 'unavailable' || error.kind === 'transport')
  );
}

/** How long after a reload request the pollers must tolerate transport drops. */
const reloadGraceMs = 10_000;

let reloadWindowUntil = 0;

/**
 * Open the reload grace window. Called the moment a reload is requested because the transport
 * recycle it triggers also drops the requests of every always-on poller.
 */
export function markReloadInFlight(): void {
  reloadWindowUntil = Date.now() + reloadGraceMs;
}

/**
 * True when `error` is a reload-induced drop and the reload grace window is still open, i.e.
 * a transient transport failure caused by a reload rather than a genuine outage. Pollers
 * should stay silent for such errors instead of reporting them.
 */
export function isReloadWindowError(error: unknown): boolean {
  return isReloadDrop(error) && Date.now() < reloadWindowUntil;
}

export const getControllerState = getState;
