<script setup lang="ts">
import { computed, ref } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { useMessage } from 'naive-ui'
import { NAlert, NButton, NCard, NDescriptions, NDescriptionsItem, NGrid, NGridItem, NSpin, NSpace, NTag } from 'naive-ui'
import ApiErrorAlert from '../components/ApiErrorAlert.vue'
import { ApiClientError } from '../api/client'
import { reloadSettings, getState } from '../api/resources/controller'
import { globalSettingsPath } from '../api/resources/globalSettings'
import { getRoot } from '../api/resources/root'
import type { ControllerListenersState, ControllerState } from '../api/types'
import { useCas } from '../composables/useCas'
import { t } from '../i18n'

const queryClient = useQueryClient()
const message = useMessage()
const cas = useCas(queryClient)
const stateQuery = useQuery({
  queryKey: ['controller', 'state'],
  queryFn: getState,
  refetchInterval: 5000,
})
const rootQuery = useQuery({
  queryKey: ['root'],
  queryFn: getRoot,
})
const reloadResult = ref<ControllerState | null>(null)

const listenerLabels: Record<keyof ControllerListenersState, string> = {
  hostRoute: 'dashboard.listener.labels.hostRoute',
  httpJson: 'dashboard.listener.labels.httpJson',
  grpc: 'dashboard.listener.labels.grpc',
  unixSocket: 'dashboard.listener.labels.unixSocket',
}
const listenerKeys = Object.keys(listenerLabels) as Array<keyof ControllerListenersState>
const listeners = computed(() => listenerKeys.map((key) => ({
  key,
  label: t(listenerLabels[key]),
  value: stateQuery.data.value?.listeners[key],
})))

const reloadMutation = useMutation({
  mutationFn: () => cas.run(globalSettingsPath, (ifMatch) => reloadSettings(ifMatch)),
  onSuccess: async (state) => {
    reloadResult.value = state
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['controller', 'state'] }),
      queryClient.invalidateQueries({ queryKey: ['root'] }),
    ])
    message.success(t('dashboard.reload.success'))
  },
})
const reloadUncertain = computed(() => {
  const error = reloadMutation.error.value
  return error instanceof ApiClientError && (error.kind === 'network' || error.kind === 'unavailable')
})
const state = computed(() => stateQuery.data.value)
const host = computed(() => state.value?.host ?? null)
const webUi = computed(() => state.value?.webUi ?? null)
const root = computed(() => rootQuery.data.value)
const stateLoading = computed(() => stateQuery.isLoading.value)
const rootLoading = computed(() => rootQuery.isLoading.value)
const reloadPending = computed(() => reloadMutation.isPending.value)

function listenerTagType(enabled: boolean, running: boolean): 'success' | 'warning' | 'error' | 'default' {
  if (running) return 'success'
  if (enabled) return 'warning'
  return 'default'
}

function listenerStateText(enabled: boolean, running: boolean): string {
  if (running) return t('dashboard.listener.state.running')
  if (enabled) return t('dashboard.listener.state.enabledNotRunning')
  return t('common.disabledState')
}
function booleanText(value: boolean): string {
  return t(value ? 'common.yes' : 'common.no')
}

function snapshotStateText(value: string): string {
  return t(`dashboard.host.snapshotStates.${value}`)
}

function readinessText(value: string): string {
  return t(`dashboard.host.readinessStates.${value}`)
}

function formatDate(value: string | null): string {
  if (!value) return t('common.unknown')
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString()
}
</script>

<template>
  <main class="page-stack">
    <header class="page-heading">
      <div>
        <h1>{{ t('dashboard.title') }}</h1>
        <p>{{ t('dashboard.subtitle') }}</p>
      </div>
      <n-button type="primary" :loading="reloadPending" @click="reloadMutation.mutate()">{{ t('dashboard.reload.button') }}</n-button>
    </header>

    <ApiErrorAlert v-if="stateQuery.isError.value" :error="stateQuery.error.value" />
    <ApiErrorAlert v-if="rootQuery.isError.value" :error="rootQuery.error.value" />
    <ApiErrorAlert v-if="reloadMutation.isError.value" :error="reloadMutation.error.value" />
    <n-alert v-if="reloadUncertain" type="warning" :show-icon="true">
      {{ t('dashboard.reload.uncertain') }}
    </n-alert>
    <n-alert v-if="reloadResult" type="success" :show-icon="true">
      {{ t(reloadResult.bootstrapMode ? 'dashboard.reload.completed.bootstrap' : 'dashboard.reload.completed.configured') }}
    </n-alert>
    <n-spin :show="stateLoading || rootLoading">
      <n-card :title="t('dashboard.listener.title')">
        <n-grid :cols="4" :x-gap="16" :y-gap="16" responsive="screen">
          <n-grid-item v-for="listener in listeners" :key="listener.key">
            <div class="listener-card">
              <strong>{{ listener.label }}</strong>
              <n-space>
                <n-tag :type="listener.value ? (listener.value.enabled ? 'success' : 'default') : 'default'" size="small">
                  {{ listener.value?.enabled ? t('common.enabledState') : t('common.disabledState') }}
                </n-tag>
                <n-tag :type="listener.value ? listenerTagType(listener.value.enabled, listener.value.running) : 'default'" size="small">
                  {{ listener.value ? listenerStateText(listener.value.enabled, listener.value.running) : t('common.unknown') }}
                </n-tag>
              </n-space>
            </div>
          </n-grid-item>
        </n-grid>
      </n-card>
    </n-spin>

    <n-card v-if="state" :title="t('dashboard.mode.title')">
      <n-tag :type="state.bootstrapMode ? 'warning' : 'success'">
        {{ t(state.bootstrapMode ? 'dashboard.mode.bootstrap' : 'dashboard.mode.configured') }}
      </n-tag>
    </n-card>

    <n-card v-if="host" :title="t('dashboard.host.title')">
      <n-descriptions bordered :column="2">
        <n-descriptions-item :label="t('dashboard.host.nodeId')">
          {{ host.nodeId ?? t('common.unknown') }}
        </n-descriptions-item>
        <n-descriptions-item :label="t('dashboard.host.readOnly')">
          {{ booleanText(host.readOnly) }}
        </n-descriptions-item>
        <n-descriptions-item :label="t('dashboard.host.extensionsSkipped')">
          {{ booleanText(host.extensionsSkipped) }}
        </n-descriptions-item>
        <n-descriptions-item :label="t('dashboard.host.supervisorDisabled')">
          {{ booleanText(host.supervisorDisabled) }}
        </n-descriptions-item>
        <n-descriptions-item :label="t('dashboard.host.databaseAvailable')">
          {{ booleanText(host.databaseAvailable) }}
        </n-descriptions-item>
        <n-descriptions-item :label="t('dashboard.host.snapshotAvailable')">
          {{ booleanText(host.snapshotAvailable) }}
        </n-descriptions-item>
        <n-descriptions-item :label="t('dashboard.host.configurationValid')">
          {{ booleanText(host.configurationValid) }}
        </n-descriptions-item>
        <n-descriptions-item :label="t('dashboard.host.publishedConfigurationVersion')">
          {{ host.publishedConfigurationVersion ?? t('common.unknown') }}
        </n-descriptions-item>
        <n-descriptions-item :label="t('dashboard.host.lastSnapshotState')">
          {{ snapshotStateText(host.lastSnapshotState) }}
        </n-descriptions-item>
        <n-descriptions-item :label="t('dashboard.host.lastSnapshotStateAt')">
          {{ formatDate(host.lastSnapshotStateAt) }}
        </n-descriptions-item>
        <n-descriptions-item :label="t('dashboard.host.readiness')">
          {{ readinessText(host.readiness) }}
        </n-descriptions-item>
      </n-descriptions>
    </n-card>

    <n-card v-if="webUi" :title="t('dashboard.webUi.title')">
      <n-descriptions bordered :column="2">
        <n-descriptions-item :label="t('dashboard.webUi.embedded')">
          <n-tag :type="webUi.embedded ? 'success' : 'default'" size="small">
            {{ booleanText(webUi.embedded) }}
          </n-tag>
        </n-descriptions-item>
        <n-descriptions-item :label="t('dashboard.webUi.enabled')">
          <n-tag :type="webUi.enabled ? 'success' : 'default'" size="small">
            {{ booleanText(webUi.enabled) }}
          </n-tag>
        </n-descriptions-item>
      </n-descriptions>
    </n-card>

    <n-spin :show="rootLoading">
      <n-card :title="t('dashboard.overview.title')">
        <n-grid :cols="4" :x-gap="16" responsive="screen">
          <n-grid-item><div class="overview-item"><span>{{ t('dashboard.overview.version') }}</span><strong>{{ root?.version ?? t('dashboard.overview.notAvailable') }}</strong></div></n-grid-item>
          <n-grid-item><div class="overview-item"><span>{{ t('dashboard.overview.routes') }}</span><strong>{{ root?.routes.length ?? t('dashboard.overview.notAvailable') }}</strong></div>
          </n-grid-item>
          <n-grid-item><div class="overview-item"><span>{{ t('dashboard.overview.services') }}</span><strong>{{ root?.services.length ?? t('dashboard.overview.notAvailable') }}</strong></div>
          </n-grid-item>
          <n-grid-item><div class="overview-item"><span>{{ t('dashboard.overview.extensions') }}</span><strong>{{ root?.extensions.length ?? t('dashboard.overview.notAvailable') }}</strong></div>
          </n-grid-item>
        </n-grid>
      </n-card>
    </n-spin>
  </main>
</template>

<style scoped>
.page-stack {
  display: flex;
  flex-direction: column;
  gap: 16px;
  margin: 0 auto;
  max-width: 1280px;
}

.page-heading {
  align-items: center;
  display: flex;
  gap: 16px;
  justify-content: space-between;
}

h1 {
  margin: 0;
}

.page-heading p {
  color: var(--n-text-color-3);
  margin: 6px 0 0;
}

.listener-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.overview-item {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.overview-item span {
  color: var(--n-text-color-3);
}

.overview-item strong {
  font-size: 1.5rem;
}
</style>
