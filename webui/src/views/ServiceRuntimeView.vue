<script setup lang="ts">
import { computed } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import {
  NButton,
  NCard,
  NDescriptions,
  NDescriptionsItem,
  NEmpty,
  NPopconfirm,
  NSpin,
  NSpace,
  NTag,
  useMessage,
} from 'naive-ui'
import ApiErrorAlert from '../components/ApiErrorAlert.vue'
import {
  getAllRuntime,
  getRuntime,
  restartServiceRuntime,
  resumeServiceRuntime,
} from '../api/resources/services'
import { ApiClientError } from '../api/client'
import { healthTagType, lifecycleLabel, lifecycleTagType } from '../serviceStatus'
import { useRoute } from 'vue-router'
import { t } from '../i18n'

const route = useRoute()
const queryClient = useQueryClient()
const message = useMessage()
const serviceId = computed(() => String(route.params.id ?? ''))
const runtimeListQuery = useQuery({
  queryKey: ['services', 'runtime'],
  queryFn: getAllRuntime,
  retry: false,
  refetchInterval: 3000,
  refetchIntervalInBackground: false,
})
// A service only has a runtime snapshot while the supervisor tracks an active generation;
// fetching one in any other state answers 404, so skip the request and show a placeholder.
const runtimeListed = computed(() => {
  const list = runtimeListQuery.data.value
  if (!list) return null
  return list.some(item => item.serviceId === serviceId.value)
})
const runtimeQuery = useQuery({
  queryKey: computed(() => ['services', serviceId.value, 'runtime']),
  queryFn: () => getRuntime(serviceId.value),
  enabled: computed(() => serviceId.value.length > 0 && runtimeListed.value !== false),
  refetchInterval: 3000,
  refetchIntervalInBackground: false,
})
const snapshot = computed(() => runtimeQuery.data.value)
const runtimeNotFound = computed(() =>
  runtimeQuery.error.value instanceof ApiClientError && runtimeQuery.error.value.status === 404)
const runtimeUnavailable = computed(() => runtimeListed.value === false || runtimeNotFound.value)

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

function openOutputWindow(): void {
  // The viewer runs as a standalone browser window on a bare route; the hash URL keeps the
  // current transport prefix (origin path) so HostRoute-hosted pages reach the same root.
  const url = `${window.location.origin}${window.location.pathname}#/services/${serviceId.value}/output`
  window.open(url, '_blank', 'popup=yes,width=960,height=640')
}
</script>


<template>
  <main class="page-stack page-stack--narrow">
    <header class="page-heading">
      <div>
        <h1>{{ t('serviceRuntime.title') }}</h1>
        <p class="service-id">{{ serviceId }}</p>
      </div>
      <n-space v-if="serviceId" align="center">
        <n-button @click="openOutputWindow">{{ t('serviceRuntime.actions.output') }}</n-button>
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
    <ApiErrorAlert v-if="runtimeQuery.isError.value && !runtimeNotFound" :error="runtimeQuery.error.value" />
    <ApiErrorAlert v-if="runtimeMutation.isError.value" :error="runtimeMutation.error.value" />
    <n-spin :show="runtimeQuery.isLoading.value">
      <n-card v-if="runtimeUnavailable">
        <n-empty :description="t('serviceRuntime.unavailable')" />
      </n-card>
      <n-card v-else-if="snapshot" :title="t('serviceRuntime.snapshotTitle')">
        <n-space wrap>
          <n-tag :type="lifecycleTagType(snapshot.lifecycleState)">
            {{ t('serviceRuntime.status.lifecycle', { state: lifecycleLabel(snapshot.lifecycleState) }) }}
          </n-tag>
          <n-tag :type="healthTagType(snapshot.healthState)">
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

.service-id {
  color: var(--n-text-color-3);
  margin: 6px 0 0;
}

.runtime-details {
  margin-top: 20px;
}
</style>
