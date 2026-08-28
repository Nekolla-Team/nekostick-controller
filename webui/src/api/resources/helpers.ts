import { ApiClientError, request, type ApiResponse, type RequestOptions } from '../client';
import { setVersion } from '../etag';

export function rememberVersion<T>(path: string, response: ApiResponse<T>): void {
  if (response.etag !== undefined) {
    setVersion(path, response.etag);
  }
}

export function requireData<T>(response: ApiResponse<T>): T {
  if (response.data === null) {
    throw new ApiClientError('The controller response did not contain the expected data.', {
      status: response.status,
      kind: 'transport',
    });
  }
  return response.data;
}

export async function readResource<T>(path: string): Promise<T> {
  const response = await request<T>('GET', path);
  rememberVersion(path, response);
  return requireData(response);
}

export async function writeResource<T>(
  method: 'POST' | 'PATCH' | 'PUT',
  path: string,
  ifMatch: string,
  body?: unknown,
  contentType?: string,
): Promise<T> {
  const options: RequestOptions = { ifMatch };
  if (body !== undefined) {
    options.body = body;
    options.contentType = contentType;
  }
  const response = await request<T>(method, path, options);
  rememberVersion(path, response);
  return requireData(response);
}

export async function deleteResource(path: string, ifMatch: string): Promise<void> {
  const response = await request<null>('DELETE', path, { ifMatch });
  rememberVersion(path, response);
}
export async function actionResource(path: string, method: 'POST' | 'DELETE' = 'POST'): Promise<void> {
  const response = await request<null>(method, path);
  rememberVersion(path, response);
}

export async function actionResourceData<T>(path: string): Promise<T> {
  const response = await request<T>('POST', path);
  rememberVersion(path, response);
  return requireData(response);
}
