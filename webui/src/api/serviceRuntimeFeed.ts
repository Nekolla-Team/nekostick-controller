import { connection } from '../stores/connection'
import { defaultControllerBaseUrl, joinHostRoute, parseApiErrorResponse, type ApiClientError } from './client'
import { runtimePath } from './resources/services'
import type { ServiceRuntimeFeedEntry } from './types'

export type ServiceRuntimeFeedFailure =
  | { kind: 'http'; status: number; error?: ApiClientError }
  | { kind: 'network' }
  | { kind: 'truncated' }

export interface ServiceRuntimeFeedHandlers {
  onOpen(): void
  onEntry(entry: ServiceRuntimeFeedEntry): void
  onEnd(): void
  onFailure(failure: ServiceRuntimeFeedFailure): void
}

export interface ServiceRuntimeFeedHandle {
  close(): void
  readonly done: Promise<void>
}

export function buildServiceRuntimeFeedUrl(): string | null {
  const configured = connection.baseUrl?.trim()
  const base = configured || defaultControllerBaseUrl()
  if (!base) return null
  return joinHostRoute(base, `${runtimePath}/stream`)
}

export function openServiceRuntimeFeed(
  apiKey: string,
  handlers: ServiceRuntimeFeedHandlers,
): ServiceRuntimeFeedHandle {
  const url = buildServiceRuntimeFeedUrl()
  const abort = new AbortController()
  const done = (async () => {
    if (!url) {
      handlers.onFailure({ kind: 'network' })
      return
    }

    let response: Response
    try {
      response = await fetch(url, {
        headers: { Accept: 'text/event-stream', 'x-nekostick-controller-key': apiKey },
        signal: abort.signal,
      })
    } catch {
      if (!abort.signal.aborted) handlers.onFailure({ kind: 'network' })
      return
    }

    if (!response.ok) {
      handlers.onFailure({
        kind: 'http',
        status: response.status,
        error: await parseApiErrorResponse(response),
      })
      return
    }

    if (!response.body) {
      handlers.onFailure({ kind: 'http', status: response.status })
      return
    }

    handlers.onOpen()
    const reader = response.body.getReader()
    const utf8 = new TextDecoder()
    let pending = ''
    let ended = false

    const dispatchFrame = (frame: string): void => {
      let eventName = ''
      const data: string[] = []
      for (const rawLine of frame.split('\n')) {
        const line = rawLine.endsWith('\r') ? rawLine.slice(0, -1) : rawLine
        if (line.startsWith(':')) continue
        if (line.startsWith('event:')) {
          eventName = line.slice(6).trim()
          continue
        }
        if (!line.startsWith('data:')) continue
        const value = line.slice(5)
        data.push(value.startsWith(' ') ? value.slice(1) : value)
      }

      if (eventName === 'end') {
        ended = true
        handlers.onEnd()
        return
      }
      if (eventName !== 'state' || data.length === 0) return

      try {
        const parsed: unknown = JSON.parse(data.join('\n'))
        if (
          parsed !== null
          && typeof parsed === 'object'
          && 'kind' in parsed
          && (parsed.kind === 'snapshot' || parsed.kind === 'removed')
          && 'serviceId' in parsed
          && typeof parsed.serviceId === 'string'
          && 'isInitialSnapshot' in parsed
          && typeof parsed.isInitialSnapshot === 'boolean'
        ) {
          handlers.onEntry(parsed as ServiceRuntimeFeedEntry)
        }
      } catch {
        // Ignore malformed state frames; the active stream can still deliver later snapshots.
      }
    }

    try {
      while (true) {
        const { value, done: streamDone } = await reader.read()
        if (streamDone) break
        pending += utf8.decode(value, { stream: true })
        let separator = pending.indexOf('\n\n')
        while (separator >= 0) {
          dispatchFrame(pending.slice(0, separator))
          pending = pending.slice(separator + 2)
          separator = pending.indexOf('\n\n')
        }
        if (ended) break
      }
    } catch {
      // Aborted by close() or a transport drop; the tail below classifies it.
    }

    if (!ended && !abort.signal.aborted) handlers.onFailure({ kind: 'truncated' })
  })()

  return {
    close: () => abort.abort(),
    done,
  }
}
