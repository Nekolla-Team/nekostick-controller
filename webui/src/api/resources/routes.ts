import type { RouteCreateBody, RouteDto, RoutePatchBody } from '../types';
import { readResource, writeResource, deleteResource } from './helpers';

export const routesPath = '/v1/routes';

function routePath(id: string): string {
  return `${routesPath}/${encodeURIComponent(id)}`;
}

export function listRoutes(): Promise<RouteDto[]> {
  return readResource<RouteDto[]>(routesPath);
}

export function getRoute(id: string): Promise<RouteDto> {
  return readResource<RouteDto>(routePath(id));
}

export function createRoute(body: RouteCreateBody, ifMatch: string): Promise<RouteDto> {
  return writeResource<RouteDto>('POST', routesPath, ifMatch, body, 'application/json');
}

export function patchRoute(id: string, body: RoutePatchBody, ifMatch: string): Promise<RouteDto> {
  return writeResource<RouteDto>('PATCH', routePath(id), ifMatch, body, 'application/merge-patch+json');
}

export function deleteRoute(id: string, ifMatch: string): Promise<void> {
  return deleteResource(routePath(id), ifMatch);
}

export const list = listRoutes;
export const get = getRoute;
export const create = createRoute;
export const patch = patchRoute;
export const remove = deleteRoute;
export { deleteRoute as delete };
