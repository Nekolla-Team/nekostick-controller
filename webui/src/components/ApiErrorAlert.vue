<script setup lang="ts">
import { computed } from 'vue'
import { NAlert } from 'naive-ui'
import { ApiClientError, type ApiErrorKind } from '../api/client'
import { t } from '../i18n'

const props = defineProps<{
  error: unknown
  title?: string
}>()

interface ErrorDetails {
  kind: ApiErrorKind | 'unknown'
  status?: number
  code?: string
  message: string
}

function describeError(error: unknown): ErrorDetails {
  if (error instanceof ApiClientError) {
    const message = error.status === 412
      ? t('errors.conflict')
      : t(`errors.byKind.${error.kind}`)
    return {
      kind: error.kind,
      status: error.status,
      code: error.code,
      message: message === `errors.byKind.${error.kind}` ? t('errors.fallback') : message,
    }
  }
  return {
    kind: 'unknown',
    message: t('errors.networkFallback'),
  }
}

const details = computed(() => describeError(props.error))
const summary = computed(() => {
  const parts: string[] = []
  if (details.value.status !== undefined) parts.push(`status=${details.value.status}`)
  if (details.value.code) parts.push(`code=${details.value.code}`)
  parts.push(`kind=${details.value.kind}`)
  return parts.join(' · ')
})
</script>

<template>
  <n-alert v-if="error" type="error" :title="title ?? t('errors.title')" :show-icon="true">
    <div>{{ details.message }}</div>
    <small>{{ summary }}</small>
  </n-alert>
</template>
