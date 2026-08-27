<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import {
  NAlert,
  NButton,
  NCard,
  NCollapse,
  NCollapseItem,
  NDynamicTags,
  NForm,
  NFormItem,
  NInputNumber,
  NRadioButton,
  NRadioGroup,
  NSpace,
  NSpin,
  NSwitch,
  useMessage,
} from 'naive-ui'
import ApiErrorAlert from '../components/ApiErrorAlert.vue'
import { getGlobalSettings, globalSettingsPath, patchGlobalSettings } from '../api/resources/globalSettings'
import type { ClientIpRatePolicy, GlobalSettings, JsonObject, ProxyRetries, ProxyTimeouts } from '../api/types'
import { buildMergePatch, useCas } from '../composables/useCas'
import { t } from '../i18n'

interface EditableSettings {
  autoPortRangeStart: number
  autoPortRangeEnd: number
  maxRequestBodyBytes: number
  maxRequestHeaderBytes: number
  maxConcurrentRequests: number
  requestReadTimeoutMs: number
  configurationPollIntervalMs: number
  trustedProxyCidrs: string[]
  proxyTimeouts: ProxyTimeouts
  clientIpRatePolicy: ClientIpRatePolicy | null
  proxyRetries: ProxyRetries
}

function defaultPolicy(): ClientIpRatePolicy {
  return {
    tokenLimit: 100,
    tokensPerPeriod: 100,
    replenishmentPeriodMs: 1000,
    queueLimit: 0,
    rejectionBehavior: 'Reject',
    retryAfterBehavior: 'None',
  }
}

function defaultTimeouts(): ProxyTimeouts {
  return {
    connectTimeoutMs: 5000,
    httpActivityTimeoutMs: 30000,
    httpTotalTimeoutMs: 120000,
    webSocketIdleTimeoutMs: 120000,
  }
}

function defaultRetries(): ProxyRetries {
  return {
    maxRetries: 0,
    initialBackoffMs: 100,
    maximumBackoffMs: 5000,
    retryOnConnectionFailure: false,
    retryOnUpstreamDisconnect: false,
  }
}

function blankEditable(): EditableSettings {
  return {
    autoPortRangeStart: 20000,
    autoPortRangeEnd: 29999,
    maxRequestBodyBytes: 1048576,
    maxRequestHeaderBytes: 65536,
    maxConcurrentRequests: 100,
    requestReadTimeoutMs: 30000,
    configurationPollIntervalMs: 5000,
    trustedProxyCidrs: [],
    proxyTimeouts: defaultTimeouts(),
    clientIpRatePolicy: null,
    proxyRetries: defaultRetries(),
  }
}

function editableFromSettings(settings: GlobalSettings): EditableSettings {
  return {
    autoPortRangeStart: settings.autoPortRangeStart,
    autoPortRangeEnd: settings.autoPortRangeEnd,
    maxRequestBodyBytes: settings.maxRequestBodyBytes,
    maxRequestHeaderBytes: settings.maxRequestHeaderBytes,
    maxConcurrentRequests: settings.maxConcurrentRequests,
    requestReadTimeoutMs: settings.requestReadTimeoutMs,
    configurationPollIntervalMs: settings.configurationPollIntervalMs,
    trustedProxyCidrs: [...settings.trustedProxyCidrs],
    proxyTimeouts: { ...settings.proxyTimeouts },
    clientIpRatePolicy: settings.clientIpRatePolicy ? { ...settings.clientIpRatePolicy } : null,
    proxyRetries: { ...settings.proxyRetries },
  }
}

function asJsonObject(settings: EditableSettings): JsonObject {
  return JSON.parse(JSON.stringify(settings)) as JsonObject
}

const queryClient = useQueryClient()
const message = useMessage()
const cas = useCas(queryClient)
const settingsQuery = useQuery({ queryKey: ['global-settings'], queryFn: getGlobalSettings })
const form = reactive<EditableSettings>(blankEditable())
const original = ref<EditableSettings | null>(null)
const formError = ref<string | null>(null)
const policyConfigured = computed(() => form.clientIpRatePolicy !== null)

watch(
  () => settingsQuery.data.value,
  (settings) => {
    if (!settings) return
    const editable = editableFromSettings(settings)
    Object.assign(form, editable, {
      proxyTimeouts: { ...editable.proxyTimeouts },
      proxyRetries: { ...editable.proxyRetries },
      trustedProxyCidrs: [...editable.trustedProxyCidrs],
      clientIpRatePolicy: editable.clientIpRatePolicy ? { ...editable.clientIpRatePolicy } : null,
    })
    original.value = editable
    formError.value = null
  },
  { immediate: true },
)

function configurePolicy(value: unknown): void {
  const enabled = value === true
  form.clientIpRatePolicy = enabled
    ? (form.clientIpRatePolicy ? { ...form.clientIpRatePolicy } : defaultPolicy())
    : null
}

const saveMutation = useMutation({
  mutationFn: () => {
    if (!original.value) throw new Error(t('globalSettings.errors.settingsNotLoaded'))
    const patch = buildMergePatch(asJsonObject(original.value), asJsonObject(form))
    return cas.run(globalSettingsPath, (ifMatch) => patchGlobalSettings(patch, ifMatch))
  },
  onSuccess: async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['global-settings'] }),
      queryClient.invalidateQueries({ queryKey: ['root'] }),
      queryClient.invalidateQueries({ queryKey: ['controller', 'state'] }),
    ])
    message.success(t('globalSettings.saveSuccess'))
  },
})

function save(): void {
  formError.value = null
  if (form.autoPortRangeStart > form.autoPortRangeEnd) {
    formError.value = t('globalSettings.validation.portRangeOrder')
    return
  }
  if (form.autoPortRangeStart < 1 || form.autoPortRangeEnd > 65535) {
    formError.value = t('globalSettings.validation.portRangeBounds')
    return
  }
  saveMutation.mutate()
}
</script>

<template>
  <main class="page-stack">
    <header class="page-heading">
      <div>
        <h1>{{ t('globalSettings.title') }}</h1>
        <p>{{ t('globalSettings.subtitle') }}</p>
      </div>
      <n-button type="primary" :loading="saveMutation.isPending.value" :disabled="!settingsQuery.data.value" @click="save">{{ t('globalSettings.save') }}</n-button>
    </header>

    <ApiErrorAlert v-if="settingsQuery.isError" :error="settingsQuery.error" />
    <ApiErrorAlert v-if="saveMutation.isError" :error="saveMutation.error" />
    <n-alert v-if="formError" type="error" :show-icon="true">{{ formError }}</n-alert>
    <n-spin :show="settingsQuery.isLoading.value">
      <n-form v-if="settingsQuery.data" label-placement="left" label-width="240">
        <n-card :title="t('globalSettings.cards.portAndRequestLimits')">
          <n-form-item :label="t('globalSettings.fields.autoPortRangeStart')"><n-input-number v-model:value="form.autoPortRangeStart" :min="1" :max="65535" /></n-form-item>
          <n-form-item :label="t('globalSettings.fields.autoPortRangeEnd')"><n-input-number v-model:value="form.autoPortRangeEnd" :min="1" :max="65535" /></n-form-item>
          <n-form-item :label="t('globalSettings.fields.maxRequestBodyBytes')"><n-input-number v-model:value="form.maxRequestBodyBytes" :min="0" /></n-form-item>
          <n-form-item :label="t('globalSettings.fields.maxRequestHeaderBytes')"><n-input-number v-model:value="form.maxRequestHeaderBytes" :min="0" /></n-form-item>
          <n-form-item :label="t('globalSettings.fields.maxConcurrentRequests')"><n-input-number v-model:value="form.maxConcurrentRequests" :min="0" /></n-form-item>
          <n-form-item :label="t('globalSettings.fields.requestReadTimeout')"><n-input-number v-model:value="form.requestReadTimeoutMs" :min="0" /></n-form-item>
          <n-form-item :label="t('globalSettings.fields.configurationPollInterval')"><n-input-number v-model:value="form.configurationPollIntervalMs" :min="0" /></n-form-item>
          <n-form-item :label="t('globalSettings.fields.trustedProxyCidrs')"><n-dynamic-tags v-model:value="form.trustedProxyCidrs" /></n-form-item>
        </n-card>

        <n-collapse class="settings-collapse">
          <n-collapse-item :title="t('globalSettings.sections.proxyTimeouts')" name="timeouts">
            <n-form-item :label="t('globalSettings.fields.proxyTimeouts.connectTimeout')"><n-input-number v-model:value="form.proxyTimeouts.connectTimeoutMs" :min="0" /></n-form-item>
            <n-form-item :label="t('globalSettings.fields.proxyTimeouts.httpActivityTimeout')"><n-input-number v-model:value="form.proxyTimeouts.httpActivityTimeoutMs" :min="0" /></n-form-item>
            <n-form-item :label="t('globalSettings.fields.proxyTimeouts.httpTotalTimeout')"><n-input-number v-model:value="form.proxyTimeouts.httpTotalTimeoutMs" :min="0" /></n-form-item>
            <n-form-item :label="t('globalSettings.fields.proxyTimeouts.webSocketIdleTimeout')"><n-input-number v-model:value="form.proxyTimeouts.webSocketIdleTimeoutMs" :min="0" /></n-form-item>
          </n-collapse-item>
          <n-collapse-item :title="t('globalSettings.sections.proxyRetries')" name="retries">
            <n-form-item :label="t('globalSettings.fields.proxyRetries.maxRetries')"><n-input-number v-model:value="form.proxyRetries.maxRetries" :min="0" /></n-form-item>
            <n-form-item :label="t('globalSettings.fields.proxyRetries.initialBackoff')"><n-input-number v-model:value="form.proxyRetries.initialBackoffMs" :min="0" /></n-form-item>
            <n-form-item :label="t('globalSettings.fields.proxyRetries.maximumBackoff')"><n-input-number v-model:value="form.proxyRetries.maximumBackoffMs" :min="0" /></n-form-item>
            <n-form-item :label="t('globalSettings.fields.proxyRetries.retryOnConnectionFailure')"><n-switch v-model:value="form.proxyRetries.retryOnConnectionFailure" /></n-form-item>
            <n-form-item :label="t('globalSettings.fields.proxyRetries.retryOnUpstreamDisconnect')"><n-switch v-model:value="form.proxyRetries.retryOnUpstreamDisconnect" /></n-form-item>
          </n-collapse-item>
          <n-collapse-item :title="t('globalSettings.sections.clientIpRatePolicy')" name="rate-policy">
            <n-form-item :label="t('globalSettings.fields.clientIpRatePolicy.status')">
              <n-radio-group :value="policyConfigured" @update:value="configurePolicy">
                <n-radio-button :value="true">{{ t('globalSettings.fields.clientIpRatePolicy.configured') }}</n-radio-button>
                <n-radio-button :value="false">{{ t('globalSettings.fields.clientIpRatePolicy.notConfigured') }}</n-radio-button>
              </n-radio-group>
            </n-form-item>
            <template v-if="form.clientIpRatePolicy">
              <n-form-item :label="t('globalSettings.fields.clientIpRatePolicy.tokenLimit')"><n-input-number v-model:value="form.clientIpRatePolicy.tokenLimit" :min="0" /></n-form-item>
              <n-form-item :label="t('globalSettings.fields.clientIpRatePolicy.tokensPerPeriod')"><n-input-number v-model:value="form.clientIpRatePolicy.tokensPerPeriod" :min="0" /></n-form-item>
              <n-form-item :label="t('globalSettings.fields.clientIpRatePolicy.replenishmentPeriod')"><n-input-number v-model:value="form.clientIpRatePolicy.replenishmentPeriodMs" :min="1" /></n-form-item>
              <n-form-item :label="t('globalSettings.fields.clientIpRatePolicy.queueLimit')"><n-input-number v-model:value="form.clientIpRatePolicy.queueLimit" :min="0" /></n-form-item>
              <n-form-item :label="t('globalSettings.fields.clientIpRatePolicy.rejectionBehavior.label')">
                <n-radio-group v-model:value="form.clientIpRatePolicy.rejectionBehavior">
                  <n-radio-button value="Reject">{{ t('globalSettings.fields.clientIpRatePolicy.rejectionBehavior.reject') }}</n-radio-button>
                  <n-radio-button value="Queue">{{ t('globalSettings.fields.clientIpRatePolicy.rejectionBehavior.queue') }}</n-radio-button>
                </n-radio-group>
              </n-form-item>
              <n-form-item :label="t('globalSettings.fields.clientIpRatePolicy.retryAfterBehavior.label')">
                <n-radio-group v-model:value="form.clientIpRatePolicy.retryAfterBehavior">
                  <n-radio-button value="None">{{ t('globalSettings.fields.clientIpRatePolicy.retryAfterBehavior.none') }}</n-radio-button>
                  <n-radio-button value="FromReplenishmentPeriod">{{ t('globalSettings.fields.clientIpRatePolicy.retryAfterBehavior.fromReplenishmentPeriod') }}</n-radio-button>
                </n-radio-group>
              </n-form-item>
            </template>
          </n-collapse-item>
        </n-collapse>
      </n-form>
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

.page-heading p {
  color: var(--n-text-color-3);
  margin: 6px 0 0;
}

.settings-collapse {
  margin-top: 16px;
}
</style>
