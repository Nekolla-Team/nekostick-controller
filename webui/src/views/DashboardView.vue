<script setup lang="ts">
import { computed, ref, watch, type Component } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { useRouter } from 'vue-router'
import { useMessage } from 'naive-ui'
import { NAlert, NButton, NCard, NDescriptions, NDescriptionsItem, NGrid, NGridItem, NIcon, NSpin, NTag } from 'naive-ui'
import ApiErrorAlert from '../components/ApiErrorAlert.vue'
import MetricChart from '../components/MetricChart.vue'
import { IconDashboard, IconExtension, IconRoute, IconService } from '../components/icons'
import { getState, getTelemetry, isReloadDrop, isReloadWindowError, reloadSettings } from '../api/resources/controller'
import { globalSettingsPath } from '../api/resources/globalSettings'
import { getRoot } from '../api/resources/root'
import type { ControllerState, ControllerTelemetry } from '../api/types'
import { useCas } from '../composables/useCas'
import { usePersistentError } from '../composables/usePersistentError'
import { t } from '../i18n'

interface Series {
  name: string
  color: string
  data: Array<number | null>
}

const router = useRouter()
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
const telemetryQuery = useQuery({
  queryKey: ['controller', 'telemetry'],
  queryFn: getTelemetry,
  refetchInterval: 1500,
})
const reloadResult = ref<ControllerState | null>(null)
// Both in-page pollers get the same two guards as the app-level state poll: silent during a
// reload window (the reload recycles their transport too), otherwise reported only after
// repeated failures so a per-poll flicker cannot toggle the alert.
const stateError = usePersistentError(stateQuery, isReloadWindowError).error
const telemetryError = usePersistentError(telemetryQuery, isReloadWindowError).error

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
const reloadUncertain = computed(() => isReloadDrop(reloadMutation.error.value))
const state = computed(() => stateQuery.data.value)
const host = computed(() => state.value?.host ?? null)
const root = computed(() => rootQuery.data.value)
const stateLoading = computed(() => stateQuery.isLoading.value)
const rootLoading = computed(() => rootQuery.isLoading.value)
const reloadPending = computed(() => reloadMutation.isPending.value)

const TELEMETRY_SAMPLE_LIMIT = 80
// Must equal telemetryQuery's refetchInterval: padding labels are back-computed with it.
const SAMPLE_PERIOD_MS = 1500
const samples = ref<ControllerTelemetry[]>([])
watch(
  () => telemetryQuery.data.value,
  (sample) => {
    if (sample === undefined) return
    const current = samples.value
    if (current.length > 0 && (current[current.length - 1]?.timestampUnixMs ?? 0) >= sample.timestampUnixMs) return
    current.push(sample)
    if (current.length > TELEMETRY_SAMPLE_LIMIT) current.splice(0, current.length - TELEMETRY_SAMPLE_LIMIT)
  },
)
const latest = computed(() => samples.value[samples.value.length - 1])

// The display window is fixed at TELEMETRY_SAMPLE_LIMIT slots: fewer stored samples render as
// leading null padding so every point keeps the same horizontal spacing from the first render.
const padCount = computed(() => Math.max(0, TELEMETRY_SAMPLE_LIMIT - samples.value.length))

function formatSampleTime(timestampUnixMs: number): string {
  return new Date(timestampUnixMs).toLocaleTimeString(undefined, { hour12: false })
}

const sampleTimes = computed(() => {
  const firstTimestamp = samples.value[0]?.timestampUnixMs ?? Date.now()
  const padding = Array.from({ length: padCount.value }, (_, index) =>
    formatSampleTime(firstTimestamp - (padCount.value - index) * SAMPLE_PERIOD_MS),
  )
  return [...padding, ...samples.value.map((s) => formatSampleTime(s.timestampUnixMs))]
})

const memorySeries = computed<Series[]>(() => {
  const padding: Array<number | null> = Array<number | null>(padCount.value).fill(null)
  const series: Series[] = [
    {
      color: '#3fb27f',
      data: [...padding, ...samples.value.map((s) => s.process.workingSetBytes)],
      name: t('dashboard.metrics.series.workingSet'),
    },
    {
      color: '#c2255c',
      data: [...padding, ...samples.value.map((s) => s.runtime.managedHeapBytes)],
      name: t('dashboard.metrics.series.managedHeap'),
    },
  ]
  if (latest.value?.host != null) {
    series.push({
      color: '#eba937',
      data: [...padding, ...samples.value.map((s) => s.host?.memoryUsedBytes ?? null)],
      name: t('dashboard.metrics.series.hostUsed'),
    })
  }
  return series
})

const cpuSeries = computed<Series[]>(() => {
  const padding: Array<number | null> = Array<number | null>(padCount.value).fill(null)
  const series: Series[] = [
    {
      color: '#c2255c',
      data: [...padding, ...samples.value.map((s) => s.process.cpuPercent)],
      name: t('dashboard.metrics.series.processCpu'),
    },
  ]
  if (latest.value?.host != null) {
    series.push({
      color: '#4c8dff',
      data: [...padding, ...samples.value.map((s) => s.host?.cpuPercent ?? null)],
      name: t('dashboard.metrics.series.hostCpu'),
    })
  }
  return series
})

interface StatCard {
  key: string
  label: string
  icon: Component
  value: string | number
  to: string | null
}

const stats = computed<StatCard[]>(() => [
  {
    key: 'version',
    label: t('dashboard.overview.version'),
    icon: IconDashboard,
    value: root.value?.version ?? t('dashboard.overview.notAvailable'),
    to: null,
  },
  {
    key: 'routes',
    label: t('dashboard.overview.routes'),
    icon: IconRoute,
    value: root.value?.routes.length ?? t('dashboard.overview.notAvailable'),
    to: '/routes',
  },
  {
    key: 'services',
    label: t('dashboard.overview.services'),
    icon: IconService,
    value: root.value?.services.length ?? t('dashboard.overview.notAvailable'),
    to: '/services',
  },
  {
    key: 'extensions',
    label: t('dashboard.overview.extensions'),
    icon: IconExtension,
    value: root.value?.extensions.length ?? t('dashboard.overview.notAvailable'),
    to: '/extensions',
  },
])

function formatBytes(value: number): string {
  if (value >= 1 << 30) return `${(value / 2 ** 30).toFixed(2)} GB`
  if (value >= 1 << 20) return `${(value / 2 ** 20).toFixed(1)} MB`
  return `${(value / 1024).toFixed(0)} KB`
}

function formatPercent(value: number): string {
  return `${value.toFixed(1)}%`
}

function formatUptime(totalSeconds: number): string {
  const seconds = Math.floor(totalSeconds)
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  if (days > 0) return `${days}d ${hours}h`
  if (hours > 0) return `${hours}h ${minutes}m`
  if (minutes > 0) return `${minutes}m ${seconds % 60}s`
  return `${seconds}s`
}

const metricTiles = computed<Array<{ label: string; value: string }>>(() => {
  const sample = latest.value
  if (!sample) return []
  return [
    { label: t('dashboard.metrics.tiles.uptime'), value: formatUptime(sample.uptimeSeconds) },
    { label: t('dashboard.metrics.tiles.threads'), value: String(sample.runtime.threadCount) },
    {
      label: t('dashboard.metrics.tiles.handles'),
      value: sample.runtime.handleCount === null ? 'N/A' : String(sample.runtime.handleCount),
    },
    {
      label: t('dashboard.metrics.tiles.gcCollections'),
      value: `${sample.runtime.gen0Collections} / ${sample.runtime.gen1Collections} / ${sample.runtime.gen2Collections}`,
    },
    {
      label: t('dashboard.metrics.tiles.gcPause'),
      value: formatPercent(sample.runtime.pauseTimePercentage),
    },
    {
      label: t('dashboard.metrics.tiles.allocated'),
      value: formatBytes(sample.runtime.totalAllocatedBytes),
    },
  ]
})

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
      <div class="heading-actions">
        <n-tag v-if="state" :type="state.bootstrapMode ? 'warning' : 'success'" round>
          {{ t(state.bootstrapMode ? 'dashboard.mode.bootstrap' : 'dashboard.mode.configured') }}
        </n-tag>
        <n-button type="primary" :loading="reloadPending" @click="reloadMutation.mutate()">{{ t('dashboard.reload.button') }}</n-button>
      </div>
    </header>

    <ApiErrorAlert v-if="stateError" :error="stateError" />
    <ApiErrorAlert v-if="rootQuery.isError.value" :error="rootQuery.error.value" />
    <ApiErrorAlert v-if="reloadMutation.isError.value && !reloadUncertain" :error="reloadMutation.error.value" />
    <ApiErrorAlert v-if="telemetryError" :error="telemetryError" />
    <n-alert v-if="reloadUncertain" type="warning" :show-icon="true">
      {{ t('dashboard.reload.uncertain') }}
    </n-alert>
    <n-alert v-if="reloadResult" type="success" :show-icon="true">
      {{ t(reloadResult.bootstrapMode ? 'dashboard.reload.completed.bootstrap' : 'dashboard.reload.completed.configured') }}
    </n-alert>

    <n-spin :show="rootLoading">
      <n-grid cols="2 m:4" :x-gap="16" :y-gap="16" responsive="screen">
        <n-grid-item v-for="stat in stats" :key="stat.key">
          <n-card
            class="stat-card"
            :class="{ 'stat-card--link': stat.to !== null }"
            size="small"
            @click="stat.to ? void router.push(stat.to) : undefined"
          >
            <div class="stat-body">
              <div class="stat-icon"><n-icon :size="20"><component :is="stat.icon" /></n-icon></div>
              <div class="stat-text">
                <strong>{{ stat.value }}</strong>
                <span>{{ stat.label }}</span>
              </div>
            </div>
          </n-card>
        </n-grid-item>
      </n-grid>
    </n-spin>

    <n-grid cols="1 l:2" :x-gap="16" :y-gap="16" responsive="screen">
      <n-grid-item>
        <n-card :title="t('dashboard.metrics.memory')" size="small">
          <MetricChart :times="sampleTimes" :series="memorySeries" :format-value="formatBytes" />
        </n-card>
      </n-grid-item>
      <n-grid-item>
        <n-card :title="t('dashboard.metrics.cpu')" size="small">
          <MetricChart :times="sampleTimes" :series="cpuSeries" :format-value="formatPercent" percent-axis />
        </n-card>
      </n-grid-item>
    </n-grid>

    <n-grid v-if="metricTiles.length > 0" cols="2 m:3 l:6" :x-gap="16" :y-gap="16" responsive="screen">
      <n-grid-item v-for="tile in metricTiles" :key="tile.label">
        <n-card size="small" class="metric-tile">
          <strong>{{ tile.value }}</strong>
          <span>{{ tile.label }}</span>
        </n-card>
      </n-grid-item>
    </n-grid>

    <n-spin :show="stateLoading">
      <n-grid v-if="host" cols="1" :x-gap="16" :y-gap="16" responsive="screen">
        <n-grid-item>
          <n-card :title="t('dashboard.host.title')">
            <n-descriptions bordered :column="1" label-placement="left" size="small">
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
        </n-grid-item>
      </n-grid>
    </n-spin>
  </main>
</template>

<style scoped>
.heading-actions {
  align-items: center;
  display: flex;
  gap: 12px;
}

.stat-card--link {
  cursor: pointer;
  transition: transform 0.15s ease, box-shadow 0.15s ease;
}

.stat-card--link:hover {
  box-shadow: 0 4px 16px rgba(0, 0, 0, 0.18);
  transform: translateY(-2px);
}

.stat-body {
  align-items: center;
  display: flex;
  gap: 14px;
}

.stat-icon {
  align-items: center;
  background: color-mix(in srgb, var(--n-primary-color) 14%, transparent);
  border-radius: 10px;
  color: var(--n-primary-color);
  display: flex;
  flex: 0 0 auto;
  height: 40px;
  justify-content: center;
  width: 40px;
}

.stat-text {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.stat-text strong {
  font-size: 1.375rem;
  font-variant-numeric: tabular-nums;
  line-height: 1.2;
}

.stat-text span {
  color: var(--n-text-color-3);
  font-size: 12px;
}

.metric-tile :deep(.n-card-content) {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.metric-tile strong {
  font-size: 1.125rem;
  font-variant-numeric: tabular-nums;
  line-height: 1.3;
}

.metric-tile span {
  color: var(--n-text-color-3);
  font-size: 12px;
}
</style>
