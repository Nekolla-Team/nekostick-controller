<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  NAlert,
  NButton,
  NCard,
  NDescriptions,
  NDescriptionsItem,
  NDynamicInput,
  NForm,
  NFormItem,
  NInput,
  NInputNumber,
  NPopconfirm,
  NSpin,
  NSwitch,
  NTag,
  useMessage,
} from 'naive-ui'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { ApiClientError } from '../api/client'
import ApiErrorAlert from '../components/ApiErrorAlert.vue'
import { getRoot } from '../api/resources/root'
import {
  extensionSettingsPath,
  getSettings,
  putSettings,
} from '../api/resources/extensions'
import { getState, isReloadDrop, reloadSettings } from '../api/resources/controller'
import { globalSettingsPath } from '../api/resources/globalSettings'
import { useCas } from '../composables/useCas'
import { connection, saveConnection, stageConnection } from '../stores/connection'
import { t } from '../i18n'
import type { ControllerListenersState, JsonObject, JsonValue } from '../api/types'

const controllerExtensionId = 'nekolla.nekostick.controller'
const settingsPath = extensionSettingsPath(controllerExtensionId)

type ConnectionMethod = 'http' | 'hostroute'

const message = useMessage()
const queryClient = useQueryClient()
const cas = useCas(queryClient)

const loading = ref(false)
const saving = ref(false)
const errorMessage = ref<string | null>(null)
const apiError = ref<ApiClientError | null>(null)
const apiErrorTitle = ref<string | null>(null)
const enableHttp = ref(false)
const httpPort = ref<number | null>(null)
const enableGrpc = ref(false)
const grpcPort = ref<number | null>(null)
const enableUnixSocket = ref(false)
const unixSocketPath = ref('')
const corsOrigins = ref<string[]>([])
const enableHostRoute = ref(false)
const enableWebUi = ref(true)
const preventSelfDisable = ref(false)
const hostRoutePath = ref('')
const apiKey = ref('')
let originalSettings: JsonObject = {}

function isObject(value: JsonValue | undefined): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function formatError(error: unknown): string {
  return error instanceof Error && error.message !== '' ? error.message : String(error)
}

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  apiError.value = null
  apiErrorTitle.value = null
  try {
    const settings = await getSettings(controllerExtensionId)
    originalSettings = isObject(settings.settings) ? settings.settings : {}
    enableHttp.value = originalSettings.enableHttpJson === true
    httpPort.value =
      typeof originalSettings.httpPort === 'number' ? originalSettings.httpPort : null
    enableGrpc.value = originalSettings.enableGrpc === true
    grpcPort.value =
      typeof originalSettings.grpcPort === 'number' ? originalSettings.grpcPort : null
    enableUnixSocket.value = originalSettings.enableUnixSocket === true
    unixSocketPath.value =
      typeof originalSettings.unixSocketPath === 'string' ? originalSettings.unixSocketPath : ''
    corsOrigins.value = Array.isArray(originalSettings.corsAllowedOrigins)
      ? originalSettings.corsAllowedOrigins.filter((origin): origin is string => typeof origin === 'string')
      : ['*']
    enableHostRoute.value = originalSettings.enableHostRoute === true
    hostRoutePath.value =
      typeof originalSettings.hostRoutePath === 'string' ? originalSettings.hostRoutePath : ''
    enableWebUi.value = originalSettings.enableWebUi !== false
    preventSelfDisable.value = originalSettings.preventSelfDisable === true
    apiKey.value =
      typeof originalSettings.apiKey === 'string' && originalSettings.apiKey !== ''
        ? originalSettings.apiKey
        : connection.apiKey ?? ''
  } catch (error: unknown) {
    if (error instanceof ApiClientError) {
      apiError.value = error
      apiErrorTitle.value = t('controllerConfig.loadFailed')
    } else {
      errorMessage.value = `${t('controllerConfig.loadFailed')}\n${formatError(error)}`
    }
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void load()
})

const stateQuery = useQuery({
  queryKey: ['controller', 'state'],
  queryFn: getState,
  refetchInterval: 5000,
})
const controllerState = computed(() => stateQuery.data.value)

const listenerKeys: Array<keyof ControllerListenersState> = ['hostRoute', 'httpJson', 'grpc', 'unixSocket']
const statusListeners = computed(() =>
  listenerKeys.map((key) => ({ key, value: controllerState.value?.listeners[key] })),
)

function listenerTone(enabled: boolean, running: boolean): 'ok' | 'warn' | 'idle' {
  if (running) return 'ok'
  if (enabled) return 'warn'
  return 'idle'
}

function listenerStateText(enabled: boolean, running: boolean): string {
  if (running) return t('controllerConfig.status.listenerState.running')
  if (enabled) return t('controllerConfig.status.listenerState.enabledNotRunning')
  return t('common.disabledState')
}

function generateApiKey(): void {
  const bytes = new Uint8Array(48)
  crypto.getRandomValues(bytes)
  apiKey.value = btoa(String.fromCharCode(...bytes))
}

function isCanonicalPath(path: string): boolean {
  return (
    path.length > 1 &&
    path.startsWith('/') &&
    !path.endsWith('/') &&
    !path.includes('//') &&
    !/[?#\0\r\n]/.test(path)
  )
}

/** Detects which connection method the stored baseUrl currently targets. */
function detectMethod(baseUrl: string, original: JsonObject): ConnectionMethod | null {
  let url: URL
  try {
    url = new URL(baseUrl)
  } catch {
    return null
  }

  const configuredPath =
    typeof original.hostRoutePath === 'string' && original.hostRoutePath !== ''
      ? original.hostRoutePath
      : null
  if (configuredPath !== null && url.pathname.startsWith(configuredPath)) {
    return 'hostroute'
  }
  if (
    original.enableHttpJson === true &&
    typeof original.httpPort === 'number' &&
    url.port === String(original.httpPort)
  ) {
    return 'http'
  }
  // Bootstrap connections use the ephemeral /controller<digits> route, which never
  // appears in the stored settings document.
  if (/^\/controller\d{8}/.test(url.pathname)) {
    return 'hostroute'
  }
  return null
}

function originOf(baseUrl: string): string | null {
  try {
    return new URL(baseUrl).origin
  } catch {
    return null
  }
}

function validate(): string | null {
  if (enableHttp.value && httpPort.value === null)
    return t('controllerConfig.portRequired', { listener: t('controllerConfig.httpListener') })
  if (enableGrpc.value && grpcPort.value === null)
    return t('controllerConfig.portRequired', { listener: t('controllerConfig.grpcListener') })
  if (enableUnixSocket.value && unixSocketPath.value.trim() === '')
    return t('controllerConfig.unixPathRequired')
  if (enableHostRoute.value && hostRoutePath.value.trim() === '')
    return t('controllerConfig.pathRequired')
  if (enableHostRoute.value && !isCanonicalPath(hostRoutePath.value.trim()))
    return t('controllerConfig.pathInvalid')
  if ((enableHttp.value || enableHostRoute.value) && apiKey.value.length < 32)
    return t('controllerConfig.apiKeyTooShort')
  return null
}

function buildSettings(): JsonObject {
  const merged: JsonObject = {
    ...originalSettings,
    enableHttpJson: enableHttp.value,
    enableHostRoute: enableHostRoute.value,
    enableWebUi: enableWebUi.value,
    preventSelfDisable: preventSelfDisable.value,
    enableGrpc: enableGrpc.value,
    enableUnixSocket: enableUnixSocket.value,
    corsAllowedOrigins: corsOrigins.value.filter((origin) => origin.trim() !== ''),
  }
  if (grpcPort.value !== null) {
    merged.grpcPort = grpcPort.value
  } else {
    delete merged.grpcPort
  }
  const socketPath = unixSocketPath.value.trim()
  if (socketPath !== '') {
    merged.unixSocketPath = socketPath
  } else {
    delete merged.unixSocketPath
  }
  if (httpPort.value !== null) {
    merged.httpPort = httpPort.value
  } else {
    delete merged.httpPort
  }
  const path = hostRoutePath.value.trim()
  if (path !== '') {
    merged.hostRoutePath = path
  } else {
    delete merged.hostRoutePath
  }
  if (apiKey.value !== '') {
    merged.apiKey = apiKey.value
  } else {
    delete merged.apiKey
  }
  return merged
}

/**
 * Points the local connection at the edited configuration: keep the current method when it
 * stays enabled, otherwise fall back to the other enabled method. Returns false when the
 * controller saved fine but the new endpoint did not answer.
 */
async function reconnect(next: JsonObject): Promise<boolean> {
  const currentMethod =
    connection.baseUrl !== null ? detectMethod(connection.baseUrl, originalSettings) : null
  let method: ConnectionMethod | null = currentMethod ?? 'http'
  const enabled = (candidate: ConnectionMethod): boolean =>
    candidate === 'http' ? next.enableHttpJson === true : next.enableHostRoute === true
  if (!enabled(method)) {
    method = enabled('http') ? 'http' : enabled('hostroute') ? 'hostroute' : null
  }
  if (method === null) {
    // Both methods were disabled and the user confirmed; the stale connection is kept so the
    // connection-lost banner can guide recovery.
    return true
  }

  const nextBaseUrl =
    method === 'http'
      ? `http://127.0.0.1:${String(next.httpPort)}`
      : `${(connection.baseUrl !== null ? originOf(connection.baseUrl) : null) ?? window.location.origin}${String(next.hostRoutePath)}`
  const nextApiKey = apiKey.value !== '' ? apiKey.value : null
  const previousBaseUrl = connection.baseUrl
  const previousApiKey = connection.apiKey
  stageConnection(nextBaseUrl, nextApiKey)
  try {
    await getRoot()
    saveConnection(nextBaseUrl, nextApiKey)
    queryClient.clear()
    return true
  } catch {
    stageConnection(previousBaseUrl, previousApiKey)
    return false
  }
}

async function save(): Promise<void> {
  if (saving.value || loading.value) return

  apiError.value = null
  apiErrorTitle.value = null
  const validationError = validate()
  if (validationError !== null) {
    errorMessage.value = validationError
    return
  }

  saving.value = true
  errorMessage.value = null
  try {
    const next = buildSettings()
    await cas.run(settingsPath, (ifMatch) =>
      putSettings(
        controllerExtensionId,
        { schemaVersion: 1, settings: next },
        ifMatch,
      ),
    )
    try {
      await cas.run(globalSettingsPath, (ifMatch) => reloadSettings(ifMatch))
    } catch (error: unknown) {
      // The settings PUT already persisted; the reload request crosses the transport it
      // recycles, so a dropped or mangled response says nothing about whether the reload
      // applied. Proceed to reconnect, which probes the new endpoint for real; any other
      // failure is a real error.
      if (!isReloadDrop(error)) throw error
    }
    if (await reconnect(next)) {
      message.success(t('controllerConfig.saved'))
    } else {
      errorMessage.value = t('controllerConfig.reconnectFailed')
    }
  } catch (error: unknown) {
    if (error instanceof ApiClientError) {
      apiError.value = error
    } else {
      errorMessage.value = formatError(error)
    }
    saving.value = false
  }
}
</script>

<template>
  <main class="page-stack page-stack--narrow">
    <header class="page-heading">
      <div>
        <h1>{{ t('controllerConfig.title') }}</h1>
        <p>{{ t('controllerConfig.subtitle') }}</p>
      </div>
      <n-popconfirm
        v-if="!enableHttp && !enableHostRoute"
        :positive-text="t('common.save')"
        :negative-text="t('common.cancel')"
        @positive-click="save"
      >
        <template #trigger>
          <n-button type="error" :loading="saving">
            {{ t('controllerConfig.save') }}
          </n-button>
        </template>
        {{ t('controllerConfig.bothDisabledConfirm') }}
      </n-popconfirm>
      <n-button v-else type="primary" :loading="saving" :disabled="loading" @click="save">
        {{ t('controllerConfig.save') }}
      </n-button>
    </header>

    <n-spin :show="stateQuery.isLoading.value">
      <n-card :title="t('controllerConfig.status.title')">
        <n-descriptions bordered :column="1" label-placement="left" size="small">
          <n-descriptions-item
            v-for="listener in statusListeners"
            :key="listener.key"
            :label="t(`controllerConfig.status.listeners.${listener.key}`)"
          >
            <span class="status-value">
              <span
                class="status-dot"
                :class="`status-dot--${listener.value ? listenerTone(listener.value.enabled, listener.value.running) : 'idle'}`"
              />
              {{ listener.value ? listenerStateText(listener.value.enabled, listener.value.running) : t('common.unknown') }}
            </span>
          </n-descriptions-item>
          <n-descriptions-item v-if="controllerState" :label="t('controllerConfig.status.webUi')">
            <n-tag :type="controllerState.webUi.embedded ? 'success' : 'default'" size="small">
              {{ t('controllerConfig.status.embedded') }}
            </n-tag>
            <n-tag :type="controllerState.webUi.enabled ? 'success' : 'default'" size="small" class="status-tag-gap">
              {{ t('controllerConfig.status.enabled') }}
            </n-tag>
          </n-descriptions-item>
        </n-descriptions>
      </n-card>
    </n-spin>

    <n-spin :show="loading">
      <n-card>
        <n-form label-placement="left" label-align="left" label-width="240">
          <n-form-item>
            <template #label>{{ t('controllerConfig.webUi') }}</template>
            <n-switch v-model:value="enableWebUi" />
          </n-form-item>
          <n-form-item v-if="!enableWebUi">
            <n-alert type="warning" :show-icon="true">
              {{ t('controllerConfig.webUiDisabledWarning') }}
            </n-alert>
          </n-form-item>
          <n-form-item>
            <template #label>{{ t('controllerConfig.preventSelfDisable') }}</template>
            <n-switch v-model:value="preventSelfDisable" />
          </n-form-item>
          <n-form-item v-if="preventSelfDisable">
            <n-alert type="info" :show-icon="true">
              {{ t('controllerConfig.preventSelfDisableHint') }}
            </n-alert>
          </n-form-item>
          <n-form-item>
            <template #label>{{ t('controllerConfig.httpListener') }}</template>
            <n-switch v-model:value="enableHttp" />
          </n-form-item>
          <n-form-item v-if="enableHttp" :label="t('controllerConfig.httpPort')">
            <n-input-number
              v-model:value="httpPort"
              :min="1"
              :max="65535"
              :show-button="false"
              style="width: 100%"
            />
          </n-form-item>
          <n-form-item>
            <template #label>{{ t('controllerConfig.grpcListener') }}</template>
            <n-switch v-model:value="enableGrpc" />
          </n-form-item>
          <n-form-item v-if="enableGrpc" :label="t('controllerConfig.grpcPort')">
            <n-input-number
              v-model:value="grpcPort"
              :min="1"
              :max="65535"
              :show-button="false"
              style="width: 100%"
            />
          </n-form-item>
          <n-form-item>
            <template #label>{{ t('controllerConfig.unixSocket') }}</template>
            <n-switch v-model:value="enableUnixSocket" />
          </n-form-item>
          <n-form-item v-if="enableUnixSocket" :label="t('controllerConfig.unixSocketPath')">
            <n-input v-model:value="unixSocketPath" placeholder="/run/nekostick/controller.sock" />
          </n-form-item>
          <n-form-item :label="t('controllerConfig.corsOrigins')">
            <n-dynamic-input
              v-model:value="corsOrigins"
              :on-create="() => ''"
            >
              <template #default="{ value, index }">
                <n-input
                  :value="value"
                  :placeholder="t('controllerConfig.corsOriginPlaceholder')"
                  @update:value="(v: string) => (corsOrigins[index] = v)"
                />
              </template>
            </n-dynamic-input>
          </n-form-item>
          <n-form-item>
            <template #label>{{ t('controllerConfig.hostRoute') }}</template>
            <n-switch v-model:value="enableHostRoute" />
          </n-form-item>
          <n-form-item v-if="enableHostRoute" :label="t('controllerConfig.hostRoutePath')">
            <n-input v-model:value="hostRoutePath" placeholder="/controller" />
          </n-form-item>
          <n-form-item :label="t('controllerConfig.apiKey')">
            <div class="api-key-row">
              <n-input
                v-model:value="apiKey"
                type="password"
                show-password-on="click"
                autocomplete="off"
              />
              <n-button @click="generateApiKey">
                {{ t('controllerConfig.apiKeyGenerate') }}
              </n-button>
            </div>
          </n-form-item>
          <n-alert
            v-if="!enableHttp && !enableHostRoute"
            type="error"
            :show-icon="true"
            class="both-disabled-warning"
          >
            {{ t('controllerConfig.bothDisabledWarning') }}
          </n-alert>
          <ApiErrorAlert
            v-if="apiError"
            :error="apiError"
            :title="apiErrorTitle ?? undefined"
            class="config-error"
          />
          <n-alert
            v-else-if="errorMessage"
            type="error"
            :show-icon="true"
            class="config-error"
          >
            {{ errorMessage }}
          </n-alert>
        </n-form>
      </n-card>
    </n-spin>
  </main>
</template>

<style scoped>
.both-disabled-warning {
  margin-top: 16px;
}

.config-error {
  margin-top: 16px;
  white-space: pre-line;
}

.api-key-row {
  display: flex;
  gap: 8px;
  width: 100%;
}

.status-value {
  align-items: center;
  display: inline-flex;
  gap: 8px;
}

.status-tag-gap {
  margin-left: 8px;
}
</style>
