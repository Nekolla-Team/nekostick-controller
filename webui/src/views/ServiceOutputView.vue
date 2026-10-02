<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { NButton, NRadioButton, NRadioGroup, NSpace, NTag } from 'naive-ui'
import { connection } from '../stores/connection'
import {
  buildServiceOutputEventStreamUrl,
  buildServiceOutputUrl,
  classifyCloseCode,
  encodeKeySubProtocol,
  openServiceOutputEventStream,
  type ServiceOutputEventStreamHandle,
  type ServiceOutputStreamKind,
} from '../api/serviceOutput'
import { t } from '../i18n'

const route = useRoute()
const serviceId = computed(() => String(route.params.id ?? ''))
const stream = ref<ServiceOutputStreamKind>('stdout')

type ConnectionStatus = 'connecting' | 'live' | 'closed' | 'error'
const status = ref<ConnectionStatus>('connecting')
const closeReason = ref<string | null>(null)
const output = ref('')
const truncated = ref(false)
const logElement = ref<HTMLElement | null>(null)

// The stream is live-only and potentially unbounded; retain a rolling tail of decoded text.
const maxRetainedChars = 256 * 1024
let socket: WebSocket | null = null
let sseHandle: ServiceOutputEventStreamHandle | null = null
let decoder: TextDecoder | null = null
let stickToBottom = true

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

function appendOutput(chunk: string): void {
  if (chunk.length === 0) return
  let next = output.value + chunk
  if (next.length > maxRetainedChars) {
    next = next.slice(next.length - maxRetainedChars)
    truncated.value = true
  }
  output.value = next
  if (stickToBottom) {
    void nextTick(() => {
      const element = logElement.value
      if (element) element.scrollTop = element.scrollHeight
    })
  }
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
function connectEventStream(apiKey: string, active: TextDecoder): void {
  const url = buildServiceOutputEventStreamUrl(serviceId.value, stream.value)
  if (!url) {
    status.value = 'error'
    closeReason.value = t('serviceOutput.closed.abnormal')
    return
  }

  const handle = openServiceOutputEventStream(url, apiKey, {
    onOpen: () => {
      if (sseHandle === handle) status.value = 'live'
    },
    onChunk: (bytes) => {
      if (sseHandle === handle) appendOutput(active.decode(bytes, { stream: true }))
    },
    onEnd: (reason) => {
      if (sseHandle !== handle) return
      sseHandle = null
      appendOutput(active.decode())
      if (decoder === active) decoder = null
      closeReason.value = t(`serviceOutput.closed.${reason}`)
      status.value = 'closed'
    },
    onFailure: () => {
      if (sseHandle !== handle) return
      sseHandle = null
      appendOutput(active.decode())
      if (decoder === active) decoder = null
      closeReason.value = t('serviceOutput.closed.abnormal')
      status.value = 'error'
    },
  })
  sseHandle = handle
}

function connect(): void {
  closeTransport()
  closeReason.value = null
  truncated.value = false

  const apiKey = connection.apiKey
  const url = buildServiceOutputUrl(serviceId.value, stream.value)
  if (!apiKey || !url) {
    status.value = 'error'
    closeReason.value = t('serviceOutput.closed.abnormal')
    return
  }

  status.value = 'connecting'
  const active = new TextDecoder('utf-8')
  decoder = active
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
    const bytes =
      event.data instanceof ArrayBuffer ? new Uint8Array(event.data) : new Uint8Array(0)
    appendOutput(active.decode(bytes, { stream: true }))
  }
  ws.onerror = () => {
    // A socket that never opened means the transport cannot serve upgrades; fall back to SSE.
    if (socket === ws && !opened) {
      socket = null
      connectEventStream(apiKey, active)
    }
  }
  ws.onclose = (event) => {
    if (socket !== ws) return
    socket = null
    if (!opened) {
      connectEventStream(apiKey, active)
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
  output.value = ''
  truncated.value = false
}

function onScroll(): void {
  const element = logElement.value
  if (!element) return
  stickToBottom = element.scrollTop + element.clientHeight >= element.scrollHeight - 40
}

watch(stream, () => connect())
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
    <pre
      v-show="output.length > 0"
      ref="logElement"
      class="output-log"
      @scroll="onScroll"
    >{{ output }}</pre>
    <div v-if="output.length === 0" class="output-empty">{{ t('serviceOutput.empty') }}</div>
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
  flex: 1;
  font-family: monospace;
  font-size: 12px;
  margin: 0;
  overflow: auto;
  padding: 8px 12px;
  white-space: pre-wrap;
  word-break: break-all;
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
