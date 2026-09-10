<script setup lang="ts">
import { computed } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import {
  NButton,
  NCard,
  NDescriptions,
  NDescriptionsItem,
  NPopconfirm,
  NSpin,
  NSpace,
  NTag,
  useMessage,
} from 'naive-ui'
import ApiErrorAlert from '../components/ApiErrorAlert.vue'
import {
  getRuntime,
  restartServiceRuntime,
  resumeServiceRuntime,
} from '../api/resources/services'
import type { ServiceHealthState, ServiceLifecycleState } from '../api/types'
import { useRoute } from 'vue-router'
import { t } from '../i18n'

const route = useRoute()
const queryClient = useQueryClient()
const message = useMessage()
const serviceId = computed(() => String(route.params.id ?? ''))
const runtimeQuery = useQuery({
  queryKey: computed(() => ['services', serviceId.value, 'runtime']),
  queryFn: () => getRuntime(serviceId.value),
  enabled: computed(() => serviceId.value.length > 0),
  refetchInterval: 3000,
  refetchIntervalInBackground: false,
})
const snapshot = computed(() => runtimeQuery.data.value)

const runtimeMutation = useMutation({
  mutationFn: ({ id, action }: { id: string; action: 'resume' | 'restart' }) =>
    action === 'resume' ? resumeServiceRuntime(id) : restartServiceRuntime(id),
  onSuccess: async (result, variables) => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['services', variables.id, 'runtime'] }),
      queryClient.invalidateQueries({ queryKey: ['services'] }),
    ])
    if (variables.action === 'resume' && result.outcome === 'ignored') {
      message.info(t('serviceRuntime.feedback.resumeIgnored'))
    } else {
      message.success(t(
        variables.action === 'restart'
          ? 'serviceRuntime.feedback.restartSuccess'
          : 'serviceRuntime.feedback.resumeSuccess',
      ))
    }
  },
})

function lifecycleType(state: ServiceLifecycleState): 'default' | 'success' | 'warning' | 'error' {
  if (state === 'Running') return 'success'
  if (state === 'Starting' || state === 'Stopping' || state === 'waiting') return 'warning'
  if (state === 'Failed') return 'error'
  return 'default'
}

function lifecycleLabel(state: ServiceLifecycleState): string {
  return state === 'waiting' ? t('serviceRuntime.status.waiting') : state
}

function healthType(state: ServiceHealthState): 'default' | 'success' | 'warning' | 'error' {
  if (state === 'Healthy') return 'success'
  if (state === 'Unhealthy') return 'error'
  return 'default'
}

function humanizeUptime(uptimeMs: number | null): string {
  if (uptimeMs === null || !Number.isFinite(uptimeMs)) return t('common.unknown')
  let remaining = Math.max(0, Math.floor(uptimeMs / 1000))
  const days = Math.floor(remaining / 86400)
  remaining %= 86400
  const hours = Math.floor(remaining / 3600)
  remaining %= 3600
  const minutes = Math.floor(remaining / 60)
  const seconds = remaining % 60
  const parts: string[] = []
  if (days) parts.push(t('serviceRuntime.uptime.days', { days }))
  if (hours || days) parts.push(t('serviceRuntime.uptime.hours', { hours }))
  if (minutes || hours || days) parts.push(t('serviceRuntime.uptime.minutes', { minutes }))
  if (!parts.length || seconds) parts.push(t('serviceRuntime.uptime.seconds', { seconds }))
  return parts.join(' ')
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
        <h1>{{ t('serviceRuntime.title') }}</h1>
        <p class="service-id">{{ serviceId }}</p>
      </div>
      <n-space v-if="serviceId" align="center">
        <n-popconfirm
          :positive-text="t('common.confirm')"
          :negative-text="t('common.cancel')"
          @positive-click="runtimeMutation.mutate({ id: serviceId, action: 'restart' })"
        >
          <template #trigger>
            <n-button :loading="runtimeMutation.isPending.value" :disabled="runtimeMutation.isPending.value">
              {{ t('serviceRuntime.actions.restart') }}
            </n-button>
          </template>
          {{ t('serviceRuntime.confirm.restart') }}
        </n-popconfirm>
        <n-button
          type="primary"
          :loading="runtimeMutation.isPending.value"
          :disabled="runtimeMutation.isPending.value"
          @click="runtimeMutation.mutate({ id: serviceId, action: 'resume' })"
        >
          {{ t('serviceRuntime.actions.resume') }}
        </n-button>
      </n-space>
    </header>
    <ApiErrorAlert v-if="runtimeQuery.isError.value" :error="runtimeQuery.error.value" />
    <ApiErrorAlert v-if="runtimeMutation.isError.value" :error="runtimeMutation.error.value" />
    <n-spin :show="runtimeQuery.isLoading.value">
      <n-card v-if="snapshot" :title="t('serviceRuntime.snapshotTitle')">
        <n-space wrap>
          <n-tag :type="lifecycleType(snapshot.lifecycleState)">
            {{ t('serviceRuntime.status.lifecycle', { state: lifecycleLabel(snapshot.lifecycleState) }) }}
          </n-tag>
          <n-tag :type="healthType(snapshot.healthState)">
            {{ t('serviceRuntime.status.health', { state: snapshot.healthState }) }}
          </n-tag>
        </n-space>
        <n-descriptions bordered :column="2" class="runtime-details">
          <n-descriptions-item :label="t('serviceRuntime.details.uptime')">
            {{ humanizeUptime(snapshot.uptimeMs) }}
          </n-descriptions-item>
          <n-descriptions-item :label="t('serviceRuntime.details.processId')">
            {{ snapshot.processId ?? t('common.unknown') }}
          </n-descriptions-item>
          <n-descriptions-item :label="t('serviceRuntime.details.forwardedRequests')">
            {{ snapshot.forwardedRequestCount }}
          </n-descriptions-item>
          <n-descriptions-item :label="t('serviceRuntime.details.activeForwarded')">
            {{ snapshot.activeForwardedRequestCount }}
          </n-descriptions-item>
          <n-descriptions-item :label="t('serviceRuntime.details.startedAt')">
            {{ formatDate(snapshot.startedAt) }}
          </n-descriptions-item>
          <n-descriptions-item :label="t('serviceRuntime.details.lastUpdatedAt')">
            {{ formatDate(snapshot.lastUpdatedAt) }}
          </n-descriptions-item>
          <n-descriptions-item :label="t('serviceRuntime.details.lastHealthAt')">
            {{ formatDate(snapshot.lastHealthAt) }}
          </n-descriptions-item>
          <n-descriptions-item :label="t('serviceRuntime.details.owner')">
            {{ snapshot.ownerExtensionId ?? t('common.unknown') }}
          </n-descriptions-item>
        </n-descriptions>
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
  max-width: 960px;
}

.page-heading {
  align-items: center;
  display: flex;
  justify-content: space-between;
}

h1 {
  margin: 0;
}

.service-id {
  color: var(--n-text-color-3);
  margin: 6px 0 0;
}

.runtime-details {
  margin-top: 20px;
}
</style>
