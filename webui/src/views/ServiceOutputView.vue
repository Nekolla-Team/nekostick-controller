<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { NButton, NRadioButton, NRadioGroup, NSpace, NTag } from 'naive-ui'
import ApiErrorAlert from '../components/ApiErrorAlert.vue'
import type { ApiClientError } from '../api/client'
import { connection } from '../stores/connection'
import {
  buildServiceOutputEventStreamUrl,
  buildServiceOutputUrl,
  classifyCloseCode,
  decodeServiceOutputChunk,
  encodeKeySubProtocol,
  openServiceOutputEventStream,
  type ServiceOutputEventStreamFailure,
  type ServiceOutputEventStreamHandle,
  type ServiceOutputStreamKind,
} from '../api/serviceOutput'
import type { ServiceLogEntry } from '../api/types'
import { lifecycleLabel } from '../serviceStatus'
import { t } from '../i18n'

const route = useRoute()
const serviceId = computed(() => String(route.params.id ?? ''))
const stream = ref<ServiceOutputStreamKind>('stdout')

type EventSeverity = 'info' | 'warning' | 'error' | 'end'
type OutputDisplayItem =
  | { id: number; kind: 'output'; text: string }
  | { id: number; kind: 'event'; severity: EventSeverity; text: string }
interface ServiceLogDisplayEvent {
  severity: EventSeverity
  message: string
}

type ConnectionStatus = 'connecting' | 'live' | 'closed' | 'error'
const status = ref<ConnectionStatus>('connecting')
const closeReason = ref<string | null>(null)
const streamError = ref<ApiClientError | null>(null)
const logItems = ref<OutputDisplayItem[]>([])
const truncated = ref(false)
const logElement = ref<HTMLElement | null>(null)

// Keep a bounded rolling tail of decoded output and lifecycle event lines.
const maxRetainedChars = 256 * 1024
let socket: WebSocket | null = null
let sseHandle: ServiceOutputEventStreamHandle | null = null
let decoder: TextDecoder | null = null
let stickToBottom = true
let retainedCharacters = 0
let nextDisplayItemId = 0
let lastSequence: number | null = null

const statusLabel = computed(() => t(`serviceOutput.status.${status.value}`))
const statusTagType = computed(() => {
  switch (status.value) {
    case 'live':
      return 'success' as const
    case 'connecting':
      return 'warning' as const
    case 'error':
      return 'error' as const
    default:
      return 'default' as const
  }
})

function scrollLogToBottom(): void {
  if (!stickToBottom) return
  void nextTick(() => {
    const element = logElement.value
    if (element) element.scrollTop = element.scrollHeight
  })
}

function trimLog(): void {
  while (retainedCharacters > maxRetainedChars) {
    const first = logItems.value[0]
    if (!first) {
      retainedCharacters = 0
      return
    }
    const excess = retainedCharacters - maxRetainedChars
    if (first.kind === 'output' && first.text.length > excess) {
      first.text = first.text.slice(excess)
      retainedCharacters -= excess
      truncated.value = true
      return
    }
    logItems.value.shift()
    retainedCharacters -= first.text.length
    truncated.value = true
  }
}

function appendOutput(chunk: string): void {
  if (chunk.length === 0) return
  const last = logItems.value[logItems.value.length - 1]
  if (last?.kind === 'output') {
    last.text += chunk
  } else {
    logItems.value.push({ id: nextDisplayItemId++, kind: 'output', text: chunk })
  }
  retainedCharacters += chunk.length
  trimLog()
  scrollLogToBottom()
}

function appendEventLine(text: string, severity: EventSeverity): void {
  logItems.value.push({ id: nextDisplayItemId++, kind: 'event', severity, text })
  retainedCharacters += text.length
  trimLog()
  scrollLogToBottom()
}

function recordSequence(sequence: number | null | undefined): void {
  if (sequence === undefined || sequence === null || !Number.isFinite(sequence)) return
  lastSequence = lastSequence === null ? sequence : Math.max(lastSequence, sequence)
}

function describeServiceLogEntry(entry: ServiceLogEntry): ServiceLogDisplayEvent | null {
  switch (entry.kind) {
    case 'generationStarted':
      return { severity: 'info', message: t('serviceOutput.events.generationStarted') }
    case 'processExited':
      return entry.processExitCode === undefined || entry.processExitCode === null
        ? { severity: 'end', message: t('serviceOutput.events.processExited') }
        : {
            severity: 'end',
            message: t('serviceOutput.events.processExitedWithCode', {
              code: entry.processExitCode,
            }),
          }
    case 'startupFailed':
      return {
        severity: 'error',
        message: t('serviceOutput.events.startupFailed', {
          reason: entry.failureReason ?? t('common.unknown'),
        }),
      }
    case 'currentState':
      return {
        severity: 'info',
        message: t('serviceOutput.events.currentState', {
          state: entry.lifecycleState == null
            ? t('common.unknown')
            : lifecycleLabel(entry.lifecycleState),
        }),
      }
    case 'gap':
      return {
        severity: 'warning',
        message: t('serviceOutput.events.gap', {
          first: entry.firstMissingSequence ?? t('common.unknown'),
          last: entry.lastMissingSequence ?? t('common.unknown'),
        }),
      }
    case 'termination': {
      const reason = entry.terminationReason
        ? t(`serviceOutput.closed.${entry.terminationReason}`)
        : t('common.unknown')
      return {
        severity: 'end',
        message: t('serviceOutput.events.termination', { reason }),
      }
    }
    case 'output':
      return null
  }
}

function appendServiceLogEntry(
  entry: ServiceLogEntry,
  sequence: number | null | undefined,
  active: TextDecoder,
): void {
  recordSequence(sequence ?? entry.sequence)
  if (entry.kind === 'output') {
    if (entry.data !== undefined && entry.data !== null) {
      appendOutput(active.decode(decodeServiceOutputChunk(entry.data), { stream: true }))
    }
    return
  }

  const event = describeServiceLogEntry(entry)
  if (!event) return
  const parsedTimestamp = new Date(entry.timestamp)
  const timestamp = Number.isNaN(parsedTimestamp.getTime())
    ? entry.timestamp
    : parsedTimestamp.toLocaleTimeString()
  appendEventLine(`${timestamp} ${event.message}`, event.severity)
}

function closeTransport(): void {
  const current = socket
  const sse = sseHandle
  socket = null
  sseHandle = null
  decoder = null
  if (
    current &&
    (current.readyState === WebSocket.CONNECTING || current.readyState === WebSocket.OPEN)
  ) {
    // Closing the socket is the unsubscribe signal; the server disposes the Host stream.
    current.close()
  }
  sse?.close()
}

/**
 * HostRoute cannot carry a WebSocket upgrade, so a socket that fails before opening falls back
 * to the SSE transport, which authenticates with a fetch header over the same path.
 */
function connectEventStream(
  apiKey: string,
  active: TextDecoder,
  activeStream: ServiceOutputStreamKind,
): void {
  const url = buildServiceOutputEventStreamUrl(serviceId.value, activeStream, lastSequence)
  if (!url) {
    status.value = 'error'
    closeReason.value = t('serviceOutput.closed.abnormal')
    return
  }

  const handle = openServiceOutputEventStream(url, apiKey, {
    onOpen: () => {
      if (sseHandle === handle) status.value = 'live'
    },
    onChunk: (bytes, sequence) => {
      if (sseHandle !== handle) return
      recordSequence(sequence)
      appendOutput(active.decode(bytes, { stream: true }))
    },
    onState: (entry, sequence) => {
      if (sseHandle === handle) appendServiceLogEntry(entry, sequence, active)
    },
    onEnd: (reason) => {
      if (sseHandle !== handle) return
      sseHandle = null
      appendOutput(active.decode())
      if (decoder === active) decoder = null
      closeReason.value = t(`serviceOutput.closed.${reason}`)
      status.value = 'closed'
    },
    onFailure: (failure) => {
      if (sseHandle !== handle) return
      sseHandle = null
      appendOutput(active.decode())
      if (decoder === active) decoder = null
      streamError.value = failure.kind === 'http' ? failure.error ?? null : null
      closeReason.value = describeStreamFailure(failure)
      status.value = 'error'
    },
  })
  sseHandle = handle
}

/** Map an SSE transport failure onto a specific, localized reason for the status banner. */
function describeStreamFailure(failure: ServiceOutputEventStreamFailure): string {
  if (failure.kind === 'network') return t('serviceOutput.failed.network')
  if (failure.kind === 'truncated') return t('serviceOutput.failed.interrupted')
  switch (failure.error?.code) {
    case 'not_running':
      return t('serviceOutput.failed.notRunning')
    case 'not_found':
      return t('serviceOutput.failed.notFound')
    case 'unauthorized':
      return t('serviceOutput.failed.unauthorized')
    case 'unsupported':
      return t('serviceOutput.failed.unsupported')
    case 'unavailable':
    case 'storage_unavailable':
      return t('serviceOutput.failed.unavailable')
    default:
      return t('serviceOutput.failed.http', { status: failure.status })
  }
}

function connect(): void {
  closeTransport()
  closeReason.value = null
  streamError.value = null
  truncated.value = false
  const apiKey = connection.apiKey
  const activeStream = stream.value
  const url = buildServiceOutputUrl(serviceId.value, activeStream)
  if (!apiKey || !url) {
    status.value = 'error'
    closeReason.value = t('serviceOutput.closed.abnormal')
    return
  }

  status.value = 'connecting'
  const active = new TextDecoder('utf-8')
  decoder = active
  if (lastSequence !== null) {
    connectEventStream(apiKey, active, activeStream)
    return
  }

  const ws = new WebSocket(url, [encodeKeySubProtocol(apiKey)])
  socket = ws
  ws.binaryType = 'arraybuffer'
  let opened = false

  ws.onopen = () => {
    if (socket !== ws) return
    opened = true
    status.value = 'live'
  }
  ws.onmessage = (event) => {
    if (socket !== ws) return
    if (typeof event.data === 'string') {
      try {
        const entry = JSON.parse(event.data) as ServiceLogEntry
        if (typeof entry.timestamp === 'string' && typeof entry.kind === 'string') {
          appendServiceLogEntry(entry, entry.sequence, active)
        }
      } catch {
        // Ignore malformed state frames; binary output remains unaffected.
      }
      return
    }
    const bytes =
      event.data instanceof ArrayBuffer ? new Uint8Array(event.data) : new Uint8Array(0)
    appendOutput(active.decode(bytes, { stream: true }))
  }
  ws.onerror = () => {
    // A socket that never opened means the transport cannot serve upgrades; fall back to SSE.
    if (socket === ws && !opened) {
      socket = null
      connectEventStream(apiKey, active, activeStream)
    }
  }
  ws.onclose = (event) => {
    if (socket !== ws) return
    socket = null
    if (!opened) {
      connectEventStream(apiKey, active, activeStream)
      return
    }
    appendOutput(active.decode())
    if (decoder === active) decoder = null
    const kind = classifyCloseCode(event.code)
    closeReason.value = t(`serviceOutput.closed.${kind}`, { code: event.code })
    status.value = 'closed'
  }
}

function reconnect(): void {
  connect()
}

function clearOutput(): void {
  logItems.value = []
  retainedCharacters = 0
  truncated.value = false
}

function onScroll(): void {
  const element = logElement.value
  if (!element) return
  stickToBottom = element.scrollTop + element.clientHeight >= element.scrollHeight - 40
}

watch([serviceId, stream], (current, previous) => {
  if (current[0] !== previous[0] || current[1] !== previous[1]) {
    lastSequence = null
    connect()
  }
})
onBeforeUnmount(() => closeTransport())
connect()
</script>

<template>
  <main class="output-page">
    <header class="output-toolbar">
      <div class="output-heading">
        <h1>{{ t('serviceOutput.title') }}</h1>
        <span class="service-id">{{ serviceId }}</span>
      </div>
      <n-space align="center" class="output-controls">
        <n-radio-group v-model:value="stream" size="small">
          <n-radio-button value="stdout">stdout</n-radio-button>
          <n-radio-button value="stderr">stderr</n-radio-button>
        </n-radio-group>
        <n-tag size="small" :type="statusTagType">{{ statusLabel }}</n-tag>
        <n-button size="small" @click="clearOutput">{{ t('serviceOutput.clear') }}</n-button>
        <n-button
          size="small"
          type="primary"
          :disabled="status === 'connecting' || status === 'live'"
          @click="reconnect"
        >
          {{ t('serviceOutput.reconnect') }}
        </n-button>
      </n-space>
    </header>
    <ApiErrorAlert v-if="streamError" :error="streamError" />
    <div
      v-show="logItems.length > 0"
      ref="logElement"
      class="output-log"
      @scroll="onScroll"
    >
      <template v-for="item in logItems" :key="item.id">
        <pre v-if="item.kind === 'output'" class="output-text">{{ item.text }}</pre>
        <div v-else class="event-line" :class="`event-line--${item.severity}`">
          {{ item.text }}
        </div>
      </template>
    </div>
    <div v-if="logItems.length === 0" class="output-empty">{{ t('serviceOutput.empty') }}</div>
    <footer class="output-footer">
      <span v-if="truncated" class="truncated-hint">{{ t('serviceOutput.truncated') }}</span>
      <span v-if="closeReason" class="close-reason">{{ closeReason }}</span>
    </footer>
  </main>
</template>

<style scoped>
.output-page {
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  gap: 8px;
  height: 100vh;
  padding: 12px 16px;
}

.output-toolbar {
  align-items: center;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  justify-content: space-between;
}

.output-heading h1 {
  font-size: 18px;
  margin: 0;
}

.service-id {
  color: rgba(128, 128, 128, 0.9);
  font-family: monospace;
  font-size: 12px;
}

.output-log {
  background: rgba(128, 128, 128, 0.08);
  border-radius: 6px;
  display: flex;
  flex: 1;
  flex-direction: column;
  font-family: monospace;
  font-size: 12px;
  overflow: auto;
  padding: 8px 12px;
  white-space: pre-wrap;
  word-break: break-all;
}

.output-text {
  font-family: inherit;
  font-size: inherit;
  margin: 0;
  white-space: pre-wrap;
  word-break: break-all;
}

.event-line {
  border-left: 2px solid currentColor;
  padding: 3px 8px;
  white-space: pre-wrap;
  word-break: break-all;
}

.event-line--info {
  color: rgba(128, 128, 128, 0.95);
}

.event-line--warning {
  color: rgba(200, 128, 32, 0.95);
}

.event-line--error {
  color: rgba(200, 64, 64, 0.95);
}

.event-line--end {
  color: rgba(128, 128, 128, 0.95);
  font-weight: 600;
}

.output-empty {
  align-items: center;
  color: rgba(128, 128, 128, 0.9);
  display: flex;
  flex: 1;
  justify-content: center;
}

.output-footer {
  display: flex;
  font-size: 12px;
  gap: 16px;
  min-height: 18px;
}

.truncated-hint {
  color: rgba(128, 128, 128, 0.9);
}

.close-reason {
  color: rgba(200, 128, 32, 0.95);
}
</style>
