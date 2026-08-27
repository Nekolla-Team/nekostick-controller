import type { RootOverview } from '../types';
import { readResource } from './helpers';

export const rootPath = '/v1';

export function getRoot(): Promise<RootOverview> {
  return readResource<RootOverview>(rootPath);
}

export const get = getRoot;
