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
  NIcon,
} from 'naive-ui'
import { ApiClientError, defaultControllerBaseUrl } from '../api/client'
import { getRoot } from '../api/resources/root'
import { connection, saveConnection, stageConnection } from '../stores/connection'
import { IconCatHead } from '../components/icons'
import ApiErrorAlert from '../components/ApiErrorAlert.vue'
import { t } from '../i18n'

const router = useRouter()
const queryClient = useQueryClient()
const baseUrl = ref(connection.baseUrl ?? '')
const apiKey = ref(connection.apiKey ?? '')
const submitting = ref(false)
const errorMessage = ref<string | null>(null)
const apiError = ref<ApiClientError | null>(null)
const locationOrigin = typeof window === 'undefined' ? '' : window.location.origin
const defaultBaseUrl = defaultControllerBaseUrl() ?? locationOrigin
const baseUrlPlaceholder = computed(() =>
  baseUrl.value.trim() ? '' : defaultBaseUrl,
)

async function handleSubmit(): Promise<void> {
  if (submitting.value) return

  submitting.value = true
  errorMessage.value = null
  apiError.value = null
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
      apiError.value = error
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
    <div class="connect-panel">
      <div class="connect-brand">
        <div class="connect-brand-mark"><n-icon :size="26"><IconCatHead /></n-icon></div>
        <h1 class="connect-brand-name">Nekostick Controller</h1>
      </div>
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
          <ApiErrorAlert v-if="apiError" :error="apiError" class="connect-error" />
          <n-alert v-else-if="errorMessage" type="error" :show-icon="true" class="connect-error">
            {{ errorMessage }}
          </n-alert>
          <p class="storage-note">
            {{ t('connect.storageNote') }}
          </p>
        </n-space>
      </n-form>
      </n-card>
    </div>
  </main>
</template>

<style scoped>
.connect-page {
  align-items: center;
  box-sizing: border-box;
  display: flex;
  justify-content: center;
  min-height: calc(100vh - 160px);
  padding: 24px;
}

.connect-panel {
  display: flex;
  flex-direction: column;
  gap: 20px;
  max-width: 420px;
  width: 100%;
}

.connect-brand {
  align-items: center;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.connect-brand-mark {
  align-items: center;
  background: linear-gradient(135deg, #f2a7c8 0%, #c084fc 100%);
  border-radius: 14px;
  box-shadow: 0 4px 18px rgba(226, 132, 178, 0.35);
  color: #2b1220;
  display: flex;
  height: 52px;
  justify-content: center;
  width: 52px;
}

.connect-brand-name {
  font-size: 1.25rem;
  font-weight: 700;
  letter-spacing: 0.01em;
  margin: 0;
}

.connect-card {
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
