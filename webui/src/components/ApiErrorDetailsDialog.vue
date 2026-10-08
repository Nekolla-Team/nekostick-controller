<script setup lang="ts">
import { computed } from 'vue'
import {
  NButton,
  NCard,
  NDescriptions,
  NDescriptionsItem,
  NModal,
  NSpace,
  NTable,
  useMessage,
} from 'naive-ui'
import type { ApiClientError } from '../api/client'
import type { ApiErrorDetails, ApiFieldError } from '../api/types'
import { t } from '../i18n'

interface DescriptionField {
  key: string
  label: string
  value: string
}

const props = defineProps<{
  error: ApiClientError
  show: boolean
}>()

const emit = defineEmits<{ 'update:show': [boolean] }>()
const message = useMessage()

const hasValue = (v: unknown): v is string => typeof v === 'string' && v.trim() !== ''

const summaryFields = computed<DescriptionField[]>(() => {
  const error = props.error
  const fields: DescriptionField[] = []
  if (error.status !== undefined) fields.push({ key: 'status', label: t('errors.dialog.status'), value: String(error.status) })
  if (hasValue(error.code)) fields.push({ key: 'code', label: t('errors.dialog.code'), value: error.code })
  if (hasValue(error.kind)) fields.push({ key: 'kind', label: t('errors.dialog.kind'), value: error.kind })
  if (hasValue(error.message)) fields.push({ key: 'message', label: t('errors.dialog.message'), value: error.message })
  if (hasValue(error.etag)) fields.push({ key: 'etag', label: t('errors.dialog.etag'), value: error.etag })
  return fields
})

const detailFields = computed<DescriptionField[]>(() => {
  const details = props.error.details
  if (!details) return []

  const fields: DescriptionField[] = []
  if (hasValue(details.reason)) fields.push({ key: 'reason', label: t('errors.dialog.reason'), value: details.reason })
  if (hasValue(details.cause)) fields.push({ key: 'cause', label: t('errors.dialog.cause'), value: details.cause })
  if (hasValue(details.parameter)) fields.push({ key: 'parameter', label: t('errors.dialog.parameter'), value: details.parameter })
  if (hasValue(details.expected)) fields.push({ key: 'expected', label: t('errors.dialog.expected'), value: details.expected })
  if (hasValue(details.actual)) fields.push({ key: 'actual', label: t('errors.dialog.actual'), value: details.actual })
  if (hasValue(details.traceId)) fields.push({ key: 'traceId', label: t('errors.dialog.traceId'), value: details.traceId })
  return fields
})

const errorRows = computed<ApiFieldError[]>(() => props.error.errors ?? [])
const report = computed(() => {
  const error = props.error
  const result: {
    status?: number
    code?: string
    kind?: string
    message?: string
    details?: Partial<ApiErrorDetails>
    errors?: ApiFieldError[]
    etag?: string
  } = {}

  if (error.status !== undefined) result.status = error.status
  if (hasValue(error.code)) result.code = error.code
  if (hasValue(error.kind)) result.kind = error.kind
  if (hasValue(error.message)) result.message = error.message
  if (detailFields.value.length > 0) {
    result.details = Object.fromEntries(detailFields.value.map(({ key, value }) => [key, value])) as Partial<ApiErrorDetails>
  }
  if (errorRows.value.length > 0) result.errors = errorRows.value
  if (hasValue(error.etag)) result.etag = error.etag
  return result
})

function updateShow(show: boolean): void {
  emit('update:show', show)
}

async function copyReport(): Promise<void> {
  if (typeof navigator === 'undefined' || !navigator.clipboard) {
    message.error(t('errors.dialog.copyFailure'))
    return
  }

  try {
    await navigator.clipboard.writeText(JSON.stringify(report.value, null, 2))
    message.success(t('errors.dialog.copySuccess'))
  } catch {
    message.error(t('errors.dialog.copyFailure'))
  }
}
</script>

<template>
  <n-modal :show="show" @update:show="updateShow">
    <n-card
      class="api-error-details-dialog"
      :title="t('errors.dialog.title')"
      :bordered="false"
      closable
      style="width: min(720px, calc(100vw - 32px));"
      @close="updateShow(false)"
    >
      <n-space vertical size="large">
        <n-descriptions v-if="summaryFields.length > 0" :column="1" bordered size="small">
          <n-descriptions-item v-for="field in summaryFields" :key="field.key" :label="field.label">
            <span class="api-error-details-dialog__value">{{ field.value }}</span>
          </n-descriptions-item>
        </n-descriptions>
        <section v-if="detailFields.length > 0">
          <h3 class="api-error-details-dialog__section-title">{{ t('errors.dialog.details') }}</h3>
          <n-descriptions :column="1" bordered size="small">
            <n-descriptions-item v-for="field in detailFields" :key="field.key" :label="field.label">
              <span class="api-error-details-dialog__value">{{ field.value }}</span>
            </n-descriptions-item>
          </n-descriptions>
        </section>
        <section v-if="errorRows.length > 0">
          <h3 class="api-error-details-dialog__section-title">{{ t('errors.dialog.fieldErrors') }}</h3>
          <n-table :bordered="false" :single-line="false" size="small">
            <thead>
              <tr>
                <th>{{ t('errors.dialog.field') }}</th>
                <th>{{ t('errors.dialog.reason') }}</th>
                <th>{{ t('errors.dialog.message') }}</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(fieldError, index) in errorRows" :key="index">
                <td class="api-error-details-dialog__value">{{ fieldError.field }}</td>
                <td class="api-error-details-dialog__value">{{ fieldError.reason }}</td>
                <td class="api-error-details-dialog__value">{{ fieldError.message }}</td>
              </tr>
            </tbody>
          </n-table>
        </section>
      </n-space>
      <template #footer>
        <n-space justify="end">
          <n-button @click="updateShow(false)">{{ t('errors.dialog.close') }}</n-button>
          <n-button type="primary" @click="copyReport">{{ t('errors.dialog.copyReport') }}</n-button>
        </n-space>
      </template>
    </n-card>
  </n-modal>
</template>

<style scoped>
.api-error-details-dialog__section-title {
  margin: 0 0 8px;
  font-size: 14px;
  font-weight: 600;
}

.api-error-details-dialog__value {
  overflow-wrap: anywhere;
  white-space: pre-wrap;
}
</style>
