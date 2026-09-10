import type {
  ServiceCreateBody,
  ServiceDto,
  ServiceEnvironment,
  ServiceEnvironmentWriteBody,
  ServicePatchBody,
  ServiceRuntimeActionResult,
  ServiceRuntimeSnapshot,
} from '../types';
import { request } from '../client';
import { readResource, writeResource, deleteResource, requireData, actionResourceData } from './helpers';

export const servicesPath = '/v1/services';
export const runtimePath = '/v1/services/runtime';

function servicePath(id: string): string {
  return `${servicesPath}/${encodeURIComponent(id)}`;
}

function environmentPath(id: string): string {
  return `${servicePath(id)}/environment`;
}

function serviceRuntimePath(id: string): string {
  return `${servicePath(id)}/runtime`;
}

export function listServices(): Promise<ServiceDto[]> {
  return readResource<ServiceDto[]>(servicesPath);
}

export function getService(id: string): Promise<ServiceDto> {
  return readResource<ServiceDto>(servicePath(id));
}

export function createService(body: ServiceCreateBody, ifMatch: string): Promise<ServiceDto> {
  return writeResource<ServiceDto>('POST', servicesPath, ifMatch, body, 'application/json');
}

export function patchService(id: string, body: ServicePatchBody, ifMatch: string): Promise<ServiceDto> {
  return writeResource<ServiceDto>('PATCH', servicePath(id), ifMatch, body, 'application/merge-patch+json');
}

export function deleteService(id: string, ifMatch: string): Promise<void> {
  return deleteResource(servicePath(id), ifMatch);
}

export function getEnvironment(id: string): Promise<ServiceEnvironment> {
  return readResource<ServiceEnvironment>(environmentPath(id));
}
export function putEnvironment(
  id: string,
  environment: Record<string, string> | ServiceEnvironmentWriteBody,
  ifMatch: string,
): Promise<ServiceEnvironment> {
  const candidate = environment as ServiceEnvironmentWriteBody;
  const body: ServiceEnvironmentWriteBody = (
    typeof candidate.environment === 'object' && candidate.environment !== null
  )
    ? candidate
    : { environment: environment as Record<string, string> };
  return writeResource<ServiceEnvironment>('PUT', environmentPath(id), ifMatch, body, 'application/json');
}

export function deleteEnvironment(id: string, ifMatch: string): Promise<void> {
  return deleteResource(environmentPath(id), ifMatch);
}

export async function getRuntime(id: string): Promise<ServiceRuntimeSnapshot> {
  const response = await request<ServiceRuntimeSnapshot>('GET', serviceRuntimePath(id));
  return requireData(response);
}
export function resumeServiceRuntime(id: string): Promise<ServiceRuntimeActionResult> {
  return actionResourceData<ServiceRuntimeActionResult>(`${serviceRuntimePath(id)}/resume`);
}

export function restartServiceRuntime(id: string): Promise<ServiceRuntimeActionResult> {
  return actionResourceData<ServiceRuntimeActionResult>(`${serviceRuntimePath(id)}/restart`);
}

export async function getAllRuntime(): Promise<ServiceRuntimeSnapshot[]> {
  const response = await request<ServiceRuntimeSnapshot[]>('GET', runtimePath);
  return requireData(response);
}

export const list = listServices;
export const get = getService;
export const create = createService;
export const patch = patchService;
export const remove = deleteService;
export { deleteService as delete };
