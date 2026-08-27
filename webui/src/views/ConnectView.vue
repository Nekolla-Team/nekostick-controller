<script setup lang="ts">
import { computed, ref } from 'vue'
import { useQueryClient } from '@tanstack/vue-query'
import { useRouter } from 'vue-router'
import {
  NAlert,
  NButton,
  NCard,
  NForm,
  NFormItem,
  NInput,
  NSpace,
} from 'naive-ui'
import { ApiClientError } from '../api/client'
import { getRoot } from '../api/resources/root'
import { connection, saveConnection, stageConnection } from '../stores/connection'
import { t } from '../i18n'

const router = useRouter()
const queryClient = useQueryClient()
const baseUrl = ref(connection.baseUrl ?? '')
const apiKey = ref(connection.apiKey ?? '')
const submitting = ref(false)
const errorMessage = ref<string | null>(null)
const locationOrigin = typeof window === 'undefined' ? '' : window.location.origin
const baseUrlPlaceholder = computed(() =>
  baseUrl.value.trim() ? '' : locationOrigin,
)

async function handleSubmit(): Promise<void> {
  if (submitting.value) return

  submitting.value = true
  errorMessage.value = null
  const nextBaseUrl = baseUrl.value.trim() || null
  const nextApiKey = apiKey.value
  const previousBaseUrl = connection.baseUrl
  const previousApiKey = connection.apiKey
  stageConnection(nextBaseUrl, nextApiKey)

  try {
    await getRoot()
    saveConnection(nextBaseUrl, nextApiKey)
    queryClient.clear()
    await router.replace('/')
  } catch (error: unknown) {
    stageConnection(previousBaseUrl, previousApiKey)
    if (error instanceof ApiClientError) {
      const reasonKey = `errors.byKind.${error.kind}`
      const reason = t(reasonKey) === reasonKey ? t('errors.fallback') : t(reasonKey)
      const detail = [
        error.status !== undefined ? `HTTP ${error.status}` : null,
        error.code ?? null,
        error.message || null,
      ]
        .filter((part) => part !== null)
        .join(' · ')
      errorMessage.value = detail
        ? `${t('connect.failed', { reason })}\n${t('connect.failedDetail', { detail })}`
        : t('connect.failed', { reason })
    } else {
      errorMessage.value = t('connect.failedUnknown', {
        message: error instanceof Error && error.message !== '' ? error.message : String(error),
      })
    }
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <main class="connect-page">
    <n-card class="connect-card" :title="t('connect.title')">
      <n-form @submit.prevent="handleSubmit">
        <n-form-item :label="t('connect.baseUrl')">
          <n-input
            v-model:value="baseUrl"
            :placeholder="baseUrlPlaceholder"
            autocomplete="url"
          />
        </n-form-item>
        <n-form-item :label="t('connect.apiKey')">
          <n-input
            v-model:value="apiKey"
            type="password"
            show-password-on="click"
            autocomplete="current-password"
          />
        </n-form-item>
        <n-space vertical>
          <n-button
            attr-type="submit"
            type="primary"
            :loading="submitting"
            block
          >
            {{ t('connect.submit') }}
          </n-button>
          <n-alert v-if="errorMessage" type="error" :show-icon="true" class="connect-error">
            {{ errorMessage }}
          </n-alert>
          <p class="storage-note">
            {{ t('connect.storageNote') }}
          </p>
        </n-space>
      </n-form>
    </n-card>
  </main>
</template>

<style scoped>
.connect-page {
  align-items: center;
  box-sizing: border-box;
  display: flex;
  justify-content: center;
  padding: 24px;
}

.connect-card {
  max-width: 480px;
  width: 100%;
}

.connect-error {
  white-space: pre-line;
}

.storage-note {
  color: var(--n-text-color-3);
  font-size: 0.875rem;
  margin: 0;
}
</style>
