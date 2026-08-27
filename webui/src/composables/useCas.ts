import { hasInjectionContext } from 'vue';
import { useQueryClient, type QueryClient } from '@tanstack/vue-query';
import { ApiClientError, request } from '../api/client';
import { invalidate, setVersion } from '../api/etag';
import type { JsonObject, JsonValue } from '../api/types';

type QueryKey = readonly unknown[]
type QueryKeys = QueryKey | readonly QueryKey[]
type QueryClientLike = Pick<QueryClient, 'invalidateQueries'>

type JsonLike = unknown;

function isObject(value: JsonLike): value is Record<string, JsonLike> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function valuesEqual(left: JsonLike, right: JsonLike): boolean {
  if (Object.is(left, right)) {
    return true;
  }
  if (Array.isArray(left) || Array.isArray(right)) {
    if (!Array.isArray(left) || !Array.isArray(right) || left.length !== right.length) {
      return false;
    }
    return left.every((value, index) => valuesEqual(value, right[index]));
  }
  if (!isObject(left) || !isObject(right)) {
    return false;
  }

  const leftKeys = Object.keys(left);
  const rightKeys = Object.keys(right);
  if (leftKeys.length !== rightKeys.length) {
    return false;
  }
  return leftKeys.every((key) => key in right && valuesEqual(left[key], right[key]));
}

function mergePatchDiff(original: JsonLike, next: JsonLike): JsonLike | undefined {
  if (!isObject(next)) {
    return valuesEqual(original, next) ? undefined : next;
  }
  if (!isObject(original)) {
    return next;
  }

  const patch: JsonObject = {};
  for (const key of Object.keys(original)) {
    if (!(key in next)) {
      patch[key] = null;
    }
  }
  for (const key of Object.keys(next)) {
    const change = mergePatchDiff(original[key], next[key]);
    if (change !== undefined) {
      patch[key] = change as JsonValue;
    }
  }
  return Object.keys(patch).length === 0 ? undefined : patch;
}

/** Build the smallest RFC-7386 merge patch that changes original into next. */
export function buildMergePatch(original: JsonObject, next: JsonObject): JsonObject {
  const patch = mergePatchDiff(original, next);
  return patch === undefined ? {} : patch as JsonObject;
}

function queryKeyForPath(path: string): QueryKeys | undefined {
  const normalizedPath = path.replace(/\/+$/, '') || '/'
  if (normalizedPath === '/v1') {
    return ['root']
  }
  if (normalizedPath === '/v1/controller/state') {
    return ['controller', 'state']
  }
  if (normalizedPath === '/v1/controller/reload-settings') {
    return ['controller', 'state']
  }
  if (normalizedPath === '/v1/global-settings') {
    return ['global-settings']
  }

  const segments = normalizedPath.split('/').filter(Boolean)
  if (segments.length < 2 || segments[0] !== 'v1') {
    return undefined
  }
  if (segments[1] === 'routes') {
    if (segments.length === 2) return ['routes']
    const routeId = decodeURIComponent(segments[2])
    return [['routes', routeId], ['routes']]
  }
  if (segments[1] === 'services') {
    if (segments.length === 2) {
      return ['services']
    }
    if (segments[2] === 'runtime' && segments.length === 3) {
      return ['services', 'runtime']
    }
    const serviceId = decodeURIComponent(segments[2])
    if (segments[3] === 'environment') {
      return ['services', serviceId, 'environment']
    }
    if (segments[3] === 'runtime') {
      return ['services', serviceId, 'runtime']
    }
    return [['services', serviceId], ['services']]
  }
  if (segments[1] === 'extensions') {
    if (segments.length === 2) {
      return ['extensions']
    }
    const extensionId = decodeURIComponent(segments[2])
    return segments[3] === 'settings'
      ? ['extensions', extensionId, 'settings']
      : ['extensions', extensionId]
  }
  return undefined
}

function queryClientFromContext(): QueryClientLike | undefined {
  if (!hasInjectionContext()) {
    return undefined;
  }
  try {
    return useQueryClient();
  } catch {
    return undefined;
  }
}

async function invalidateConflictQuery(path: string, queryClient: QueryClientLike | undefined): Promise<void> {
  invalidate(path)
  const queryKeys = queryKeyForPath(path)
  if (queryClient === undefined || queryKeys === undefined) return
  const keys = Array.isArray(queryKeys[0])
    ? queryKeys as readonly QueryKey[]
    : [queryKeys as QueryKey]
  await Promise.all(keys.map((queryKey) => queryClient.invalidateQueries({ queryKey })))
}

function isConflict(error: unknown): boolean {
  return error instanceof ApiClientError
    ? error.kind === 'conflict'
    : isObject(error) && error.kind === 'conflict';
}

export function useCas(queryClient?: QueryClientLike): {
  run<T>(path: string, mutate: (ifMatch: string) => Promise<T> | T): Promise<T>;
} {
  const client = queryClient ?? queryClientFromContext();

  async function run<T>(path: string, mutate: (ifMatch: string) => Promise<T> | T): Promise<T> {
    const latest = await request('GET', path);
    const ifMatch = latest.etag;
    if (ifMatch === undefined) {
      throw new ApiClientError('The latest resource version is unavailable.', {
        status: latest.status,
        kind: 'precondition_required',
      });
    }
    setVersion(path, ifMatch);

    try {
      return await mutate(ifMatch);
    } catch (error: unknown) {
      if (isConflict(error)) {
        try {
          await invalidateConflictQuery(path, client);
        } catch {
          // Keep the original conflict visible even if cache invalidation fails.
        }
      }
      throw error;
    }
  }

  return { run };
}
