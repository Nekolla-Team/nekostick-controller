import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { installExtensionPackage, getSettings } from '../src/api/resources/extensions'
import { saveConnection } from '../src/stores/connection'

type ProgressHandler = ((event: ProgressEvent<EventTarget>) => void) | null

class MockXmlHttpRequest {
  static instances: MockXmlHttpRequest[] = []

  readonly upload = { onprogress: null as ProgressHandler }
  readonly headers: Record<string, string> = {}
  method = ''
  url = ''
  body: unknown = undefined
  status = 0
  responseText = ''
  onload: (() => void) | null = null
  onerror: (() => void) | null = null
  onabort: (() => void) | null = null

  constructor() {
    MockXmlHttpRequest.instances.push(this)
  }

  open(method: string, url: string): void {
    this.method = method
    this.url = url
  }

  setRequestHeader(name: string, value: string): void {
    this.headers[name] = value
  }

  send(body: unknown): void {
    this.body = body
  }
}

function envelope<T>(data: T): string {
  return JSON.stringify({
    apiVersion: 1,
    ok: true,
    code: 'ok',
    message: 'The operation completed.',
    data,
    version: null,
  })
}

function errorEnvelope(code: string, message: string): string {
  return JSON.stringify({
    apiVersion: 1,
    ok: false,
    code,
    message,
    data: null,
    version: null,
  })
}

describe('extension settings resource', () => {
  beforeEach(() => {
    saveConnection('http://127.0.0.1:48123', 'test-key')
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('reports an absent settings document as an empty document', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(errorEnvelope(
      'no_settings',
      'The extension settings document has not been created.',
    ), { status: 404, headers: { ETag: '"9"' } })))

    await expect(getSettings('nekolla.sample')).resolves.toEqual({
      extensionId: 'nekolla.sample',
      schemaVersion: 1,
      settings: {},
      version: 0,
    })
  })

  it('keeps an unknown extension record an error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(
      errorEnvelope('not_found', 'The management resource was not found.'),
      { status: 404 },
    )))

    await expect(getSettings('nekolla.ghost')).rejects.toMatchObject({ kind: 'not_found' })
  })
})

describe('extension package upload resource', () => {
  beforeEach(() => {
    saveConnection('http://127.0.0.1:48123', 'test-key')
    MockXmlHttpRequest.instances = []
    vi.stubGlobal('XMLHttpRequest', MockXmlHttpRequest)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('uploads raw package bytes, unwraps the envelope, and reports progress', async () => {
    const file = new File(['zip bytes'], 'sample.zip', { type: 'application/zip' })
    const onProgress = vi.fn()
    const resultPromise = installExtensionPackage(file, onProgress)
    const upload = MockXmlHttpRequest.instances[0]

    expect(upload.method).toBe('POST')
    expect(upload.url).toBe('http://127.0.0.1:48123/v1/extensions/install')
    expect(upload.headers).toEqual({
      'Content-Type': 'application/zip',
      'x-nekostick-controller-key': 'test-key',
    })
    expect(upload.body).toBe(file)

    upload.upload.onprogress?.({ lengthComputable: true, loaded: 25, total: 100 } as ProgressEvent<EventTarget>)
    upload.upload.onprogress?.({ lengthComputable: false, loaded: 0, total: 0 } as ProgressEvent<EventTarget>)
    upload.status = 200
    upload.responseText = envelope({ id: 'sample', version: '1.2.3', replaced: true })
    upload.onload?.()

    await expect(resultPromise).resolves.toEqual({ id: 'sample', version: '1.2.3', replaced: true })
    expect(onProgress).toHaveBeenNthCalledWith(1, 0.25)
    expect(onProgress).toHaveBeenNthCalledWith(2, Number.NaN)
  })

  it('propagates the standard envelope error code and message', async () => {
    const resultPromise = installExtensionPackage(new File(['zip bytes'], 'sample.zip'), vi.fn())
    const upload = MockXmlHttpRequest.instances[0]
    upload.status = 409
    upload.responseText = errorEnvelope('downgrade_forbidden', 'The installed version is newer.')
    upload.onload?.()

    await expect(resultPromise).rejects.toMatchObject({
      status: 409,
      code: 'downgrade_forbidden',
      message: 'The installed version is newer.',
      kind: 'conflict',
    })
  })

  it('rejects XHR failures as network errors', async () => {
    const resultPromise = installExtensionPackage(new File(['zip bytes'], 'sample.zip'), vi.fn())
    MockXmlHttpRequest.instances[0].onerror?.()

    await expect(resultPromise).rejects.toMatchObject({ kind: 'network' })
  })
})
