<script setup lang="ts">
import { computed, ref } from 'vue'
import { NAlert, NButton } from 'naive-ui'
import { ApiClientError, type ApiErrorKind } from '../api/client'
import ApiErrorDetailsDialog from './ApiErrorDetailsDialog.vue'
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

const errorDetails = computed(() => describeError(props.error))
const apiError = computed(() => props.error instanceof ApiClientError ? props.error : null)
const diagnosticLine = computed(() => {
  const error = apiError.value
  if (error === null) return ''
  const parts: string[] = []
  if (error.status !== undefined) parts.push(`${t('errors.dialog.status')}: ${error.status}`)
  if (error.code) parts.push(`${t('errors.dialog.code')}: ${error.code}`)
  if (error.details?.reason) parts.push(`${t('errors.dialog.reason')}: ${error.details.reason}`)
  if (error.details?.cause) parts.push(`${t('errors.dialog.cause')}: ${error.details.cause}`)
  return parts.join(' · ')
})
const combinedLine = computed(() => {
  const error = apiError.value
  if (error === null) return ''
  return [error.message, diagnosticLine.value].filter(Boolean).join(' · ')
})

const showDetails = ref(false)
</script>

<template>
  <n-alert v-if="error" type="error" :title="title ?? t('errors.title')" :show-icon="true">
    <div>{{ errorDetails.message }}</div>
    <small v-if="combinedLine">{{ combinedLine }}</small>
    <n-button v-if="apiError" size="small" text type="primary" style="margin-left: 0.75em" @click="showDetails = true">
      {{ t('errors.dialog.open') }}
    </n-button>
    <ApiErrorDetailsDialog v-if="apiError" v-model:show="showDetails" :error="apiError" />
  </n-alert>
</template>
