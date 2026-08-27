<script setup lang="ts">
import { computed, h, reactive, ref } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import {
  NAlert,
  NButton,
  NCard,
  NCollapse,
  NCollapseItem,
  NDataTable,
  NDynamicTags,
  NForm,
  NFormItem,
  NGrid,
  NGridItem,
  NInput,
  NInputNumber,
  NModal,
  NPopconfirm,
  NRadioButton,
  NRadioGroup,
  NSpace,
  NSelect,
  NStep,
  NSteps,
  NSpin,
  NSwitch,
} from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import ApiErrorAlert from '../components/ApiErrorAlert.vue'
import { createRoute, deleteRoute, listRoutes, patchRoute, routesPath } from '../api/resources/routes'
import type {
  ForwardingMode,
  HeaderRewriteOperation,
  HeaderRewriteWrite,
  RouteCreateBody,
  RouteDto,
  RouteMatcherType,
  RoutePatchBody,
  RouteTargetType,
  RouteTargetWrite,
} from '../api/types'
import { useCas } from '../composables/useCas'
import { t } from '../i18n'

interface RouteForm {
  enabled: boolean
  matcherType: RouteMatcherType
  pattern: string
  hostPatterns: string[]
  methods: string[]
  targetType: RouteTargetType
  serviceId: string
  rootPath: string
  handlerId: string
  forwardingMode: ForwardingMode
  replaceTemplate: string
  priority: number | null
  requestHeaderRewrites: HeaderRewriteWrite[]
  responseHeaderRewrites: HeaderRewriteWrite[]
  metadataJson: string
  maxRequestBodyBytes: number | null
  maxRequestHeaderBytes: number | null
  maxConcurrentRequests: number | null
  requestReadTimeoutMs: number | null
}

const matcherTypes = computed<Array<{ label: string; value: RouteMatcherType }>>(() => [
  { label: t('routes.options.matcher.exact'), value: 'Exact' },
  { label: t('routes.options.matcher.exactCaseInsensitive'), value: 'ExactCaseInsensitive' },
  { label: t('routes.options.matcher.prefix'), value: 'Prefix' },
  { label: t('routes.options.matcher.prefixCaseInsensitive'), value: 'PrefixCaseInsensitive' },
  { label: t('routes.options.matcher.regex'), value: 'Regex' },
])
const targetTypes = computed<Array<{ label: string; value: RouteTargetType }>>(() => [
  { label: t('routes.options.target.microservice'), value: 'Microservice' },
  { label: t('routes.options.target.staticFile'), value: 'StaticFile' },
  { label: t('routes.options.target.extensionHandler'), value: 'ExtensionHandler' },
])
const forwardingModes = computed<Array<{ label: string; value: ForwardingMode }>>(() => [
  { label: t('routes.options.forwarding.preserve'), value: 'Preserve' },
  { label: t('routes.options.forwarding.strip'), value: 'Strip' },
  { label: t('routes.options.forwarding.replace'), value: 'Replace' },
])
const rewriteOperations = computed<Array<{ label: string; value: HeaderRewriteOperation }>>(() => [
  { label: t('routes.options.rewriteOperation.set'), value: 'Set' },
  { label: t('routes.options.rewriteOperation.add'), value: 'Add' },
  { label: t('routes.options.rewriteOperation.remove'), value: 'Remove' },
])

type RoutePreset = 'apiProxy' | 'staticSite' | 'extensionHandler'

function applyRoutePreset(preset: RoutePreset): void {
  if (preset === 'apiProxy') {
    form.matcherType = 'Prefix'
    form.pattern = '/api/'
    form.targetType = 'Microservice'
    form.forwardingMode = 'Strip'
  } else if (preset === 'staticSite') {
    form.matcherType = 'Prefix'
    form.pattern = '/'
    form.targetType = 'StaticFile'
    form.forwardingMode = 'Preserve'
  } else {
    form.matcherType = 'Prefix'
    form.pattern = '/ext/'
    form.targetType = 'ExtensionHandler'
    form.forwardingMode = 'Preserve'
  }
}

const matcherTypeHint = computed(() => ({
  Exact: t('routes.hints.matcherExact'),
  ExactCaseInsensitive: t('routes.hints.matcherExactCaseInsensitive'),
  Prefix: t('routes.hints.matcherPrefix'),
  PrefixCaseInsensitive: t('routes.hints.matcherPrefixCaseInsensitive'),
  Regex: t('routes.hints.matcherRegex'),
} as Record<RouteMatcherType, string>)[form.matcherType])

const patternExample = computed(() => form.matcherType === 'Regex'
  ? t('routes.hints.patternRegexExample')
  : form.matcherType.startsWith('Prefix')
    ? t('routes.hints.patternPrefixExample')
    : t('routes.hints.patternExactExample'))

const targetTypeHint = computed(() => ({
  Microservice: t('routes.hints.targetMicroservice'),
  StaticFile: t('routes.hints.targetStaticFile'),
  ExtensionHandler: t('routes.hints.targetExtensionHandler'),
} as Record<RouteTargetType, string>)[form.targetType])

const forwardingModeHint = computed(() => ({
  Preserve: t('routes.hints.forwardingPreserve'),
  Strip: t('routes.hints.forwardingStrip'),
  Replace: t('routes.hints.forwardingReplace'),
} as Record<ForwardingMode, string>)[form.forwardingMode])

function blankForm(): RouteForm {
  return {
    enabled: true,
    matcherType: 'Prefix',
    pattern: '/',
    hostPatterns: [],
    methods: [],
    targetType: 'Microservice',
    serviceId: '',
    rootPath: '',
    handlerId: '',
    forwardingMode: 'Preserve',
    replaceTemplate: '',
    priority: 100,
    requestHeaderRewrites: [],
    responseHeaderRewrites: [],
    metadataJson: '{}',
    maxRequestBodyBytes: null,
    maxRequestHeaderBytes: null,
    maxConcurrentRequests: null,
    requestReadTimeoutMs: null,
  }
}

function routeResourcePath(id: string): string {
  return `${routesPath}/${encodeURIComponent(id)}`
}

function formFromRoute(route: RouteDto): RouteForm {
  return {
    enabled: route.enabled,
    matcherType: route.matcher.type,
    pattern: route.matcher.pattern,
    hostPatterns: [...route.matcher.hostPatterns],
    methods: [...route.matcher.methods],
    targetType: route.target.type,
    serviceId: route.target.serviceId ?? '',
    rootPath: route.target.rootPath ?? '',
    handlerId: route.target.handlerId ?? '',
    forwardingMode: route.forwarding.mode,
    replaceTemplate: route.forwarding.replaceTemplate ?? '',
    priority: route.priority,
    requestHeaderRewrites: route.requestHeaderRewrites.map((item) => ({ ...item })),
    responseHeaderRewrites: route.responseHeaderRewrites.map((item) => ({ ...item })),
    metadataJson: route.metadataJson,
    maxRequestBodyBytes: route.maxRequestBodyBytes,
    maxRequestHeaderBytes: route.maxRequestHeaderBytes,
    maxConcurrentRequests: route.maxConcurrentRequests,
    requestReadTimeoutMs: route.requestReadTimeoutMs,
  }
}

const queryClient = useQueryClient()
const cas = useCas(queryClient)
const routesQuery = useQuery({
  queryKey: ['routes'],
  queryFn: listRoutes,
})
const rows = computed(() => routesQuery.data.value ?? [])
const currentStep = ref(1)
const showForm = ref(false)
const editingId = ref<string | null>(null)
const formError = ref<string | null>(null)
const form = reactive<RouteForm>(blankForm())

function resetForm(): void {
  Object.assign(form, blankForm())
  currentStep.value = 1
  formError.value = null
}

function openCreate(): void {
  editingId.value = null
  resetForm()
  showForm.value = true
}

function openEdit(route: RouteDto): void {
  editingId.value = route.id
  Object.assign(form, formFromRoute(route))
  currentStep.value = 1
  formError.value = null
  showForm.value = true
}
function nextStep(): void {
  if (currentStep.value < 4) currentStep.value += 1
}

function previousStep(): void {
  if (currentStep.value > 1) currentStep.value -= 1
}

function addRewrite(target: 'requestHeaderRewrites' | 'responseHeaderRewrites'): void {
  form[target].push({ operation: 'Set', name: '', value: '' })
}

function removeRewrite(target: 'requestHeaderRewrites' | 'responseHeaderRewrites', index: number): void {
  form[target].splice(index, 1)
}

function validateMetadata(): boolean {
  try {
    const parsed: unknown = JSON.parse(form.metadataJson)
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      formError.value = t('routes.validation.metadataJsonObject')
      return false
    }
  } catch {
    formError.value = t('routes.validation.metadataJsonInvalid')
    return false
  }
  return true
}

function buildBody(): RouteCreateBody {
  const target: RouteTargetWrite = { type: form.targetType }
  if (form.targetType === 'Microservice') target.serviceId = form.serviceId.trim() || null
  if (form.targetType === 'StaticFile') target.rootPath = form.rootPath.trim() || null
  if (form.targetType === 'ExtensionHandler') target.handlerId = form.handlerId.trim() || null
  return {
    enabled: form.enabled,
    matcher: {
      type: form.matcherType,
      pattern: form.pattern.trim(),
      hostPatterns: [...form.hostPatterns],
      methods: [...form.methods],
    },
    target,
    priority: form.priority ?? 0,
    forwarding: {
      mode: form.forwardingMode,
      replaceTemplate: form.forwardingMode === 'Replace' ? form.replaceTemplate : null,
    },
    requestHeaderRewrites: form.requestHeaderRewrites.map((item) => ({ ...item })),
    responseHeaderRewrites: form.responseHeaderRewrites.map((item) => ({ ...item })),
    metadataJson: form.metadataJson,
    clientIpRatePolicy: null,
    maxRequestBodyBytes: form.maxRequestBodyBytes,
    maxRequestHeaderBytes: form.maxRequestHeaderBytes,
    maxConcurrentRequests: form.maxConcurrentRequests,
    requestReadTimeoutMs: form.requestReadTimeoutMs,
    proxyRetries: null,
  }
}

const saveMutation = useMutation({
  mutationFn: () => {
    const body = buildBody()
    if (editingId.value) {
      const id = editingId.value
      return cas.run(routeResourcePath(id), (ifMatch) => patchRoute(id, body as RoutePatchBody, ifMatch))
    }
    return cas.run(routesPath, (ifMatch) => createRoute(body, ifMatch))
  },
  onSuccess: async () => {
    showForm.value = false
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['routes'] }),
      queryClient.invalidateQueries({ queryKey: ['root'] }),
    ])
  },
})

const toggleMutation = useMutation({
  mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
    cas.run(routeResourcePath(id), (ifMatch) => patchRoute(id, { enabled }, ifMatch)),
  onSuccess: async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['routes'] }),
      queryClient.invalidateQueries({ queryKey: ['root'] }),
    ])
  },
})

const deleteMutation = useMutation({
  mutationFn: (id: string) => cas.run(routeResourcePath(id), (ifMatch) => deleteRoute(id, ifMatch)),
  onSuccess: async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['routes'] }),
      queryClient.invalidateQueries({ queryKey: ['root'] }),
    ])
  },
})

function toggleRoute(route: RouteDto, enabled: boolean): void {
  if (toggleMutation.isPending.value) return
  toggleMutation.mutate({ id: route.id, enabled })
}

function save(): void {
  formError.value = null
  if (!form.pattern.trim()) {
    formError.value = t('routes.validation.matcherPatternRequired')
    return
  }
  if (form.targetType === 'Microservice' && !form.serviceId.trim()) {
    formError.value = t('routes.validation.microserviceServiceIdRequired')
    return
  }
  if (form.targetType === 'StaticFile' && !form.rootPath.trim()) {
    formError.value = t('routes.validation.staticFileRootPathRequired')
    return
  }
  if (form.targetType === 'ExtensionHandler' && !form.handlerId.trim()) {
    formError.value = t('routes.validation.extensionHandlerIdRequired')
    return
  }
  if (form.forwardingMode === 'Replace' && !form.replaceTemplate.trim()) {
    formError.value = t('routes.validation.replaceTemplateRequired')
    return
  }
  if (!validateMetadata()) return
  saveMutation.mutate()
}

function matcherText(route: RouteDto): string {
  return `${route.matcher.type}: ${route.matcher.pattern}`
}

function targetText(route: RouteDto): string {
  const value = route.target.serviceId ?? route.target.rootPath ?? route.target.handlerId
  return value ? `${route.target.type}: ${value}` : route.target.type
}

const columns = computed<DataTableColumns<RouteDto>>(() => [
  {
    title: t('common.enabled'),
    key: 'enabled',
    width: 80,
    render: (row) => h(NSwitch, {
      value: row.enabled,
      loading: toggleMutation.isPending.value,
      'onUpdate:value': (value: boolean) => toggleRoute(row, value),
    }),
  },
  { title: t('routes.columns.matcher'), key: 'matcher', render: matcherText },
  { title: t('routes.columns.target'), key: 'target', render: targetText },
  { title: t('routes.columns.priority'), key: 'priority', width: 100 },
  {
    title: t('common.actions'),
    key: 'actions',
    width: 180,
    render: (row) => h(NSpace, { size: 'small' }, {
      default: () => [
        h(NButton, { size: 'small', onClick: () => openEdit(row) }, { default: () => t('common.edit') }),
        h(NPopconfirm, {
          positiveText: t('common.delete'),
          negativeText: t('common.cancel'),
          onPositiveClick: () => deleteMutation.mutate(row.id),
        }, {
          trigger: () => h(NButton, { size: 'small', type: 'error', loading: deleteMutation.isPending.value }, { default: () => t('common.delete') }),
          default: () => t('routes.confirm.deleteRoute'),
        }),
      ],
    }),
  },
])
</script>

<template>
  <main class="page-stack">
    <header class="page-heading">
      <div>
        <h1>{{ t('routes.title') }}</h1>
        <p>{{ t('routes.subtitle') }}</p>
      </div>
      <n-button type="primary" @click="openCreate">{{ t('routes.create') }}</n-button>
    </header>

    <ApiErrorAlert v-if="routesQuery.isError.value" :error="routesQuery.error.value" />
    <ApiErrorAlert v-if="toggleMutation.isError.value" :error="toggleMutation.error.value" />
    <ApiErrorAlert v-if="deleteMutation.isError.value" :error="deleteMutation.error.value" />
    <n-spin :show="routesQuery.isLoading.value">
      <n-card>
        <n-data-table :columns="columns" :data="rows" :bordered="false" :single-line="false" />
      </n-card>
    </n-spin>

    <n-modal v-model:show="showForm">
      <n-card class="form-modal" :title="editingId ? t('routes.modal.editTitle') : t('routes.modal.createTitle')" closable @close="showForm = false">
        <n-form @submit.prevent="save">
          <div v-if="!editingId" class="preset-row">
            <span class="preset-label">{{ t('routes.presets.label') }}</span>
            <n-button size="tiny" tertiary @click="applyRoutePreset('apiProxy')">{{ t('routes.presets.apiProxy') }}</n-button>
            <n-button size="tiny" tertiary @click="applyRoutePreset('staticSite')">{{ t('routes.presets.staticSite') }}</n-button>
            <n-button size="tiny" tertiary @click="applyRoutePreset('extensionHandler')">{{ t('routes.presets.extensionHandler') }}</n-button>
          </div>
          <n-steps :current="currentStep" size="small" class="form-steps">
            <n-step :title="t('routes.steps.matcher')" />
            <n-step :title="t('routes.steps.target')" />
            <n-step :title="t('routes.steps.forwarding')" />
            <n-step :title="t('routes.steps.advanced')" />
          </n-steps>
          <n-form-item :label="t('common.enabled')">
            <n-switch v-model:value="form.enabled" />
          </n-form-item>
          <div v-if="currentStep === 1">
            <n-card size="small" :title="t('routes.form.matcher.title')">
              <n-form-item :label="t('routes.form.type')">
                <n-radio-group v-model:value="form.matcherType">
                  <n-radio-button v-for="option in matcherTypes" :key="option.value" :value="option.value">
                    {{ option.label }}
                  </n-radio-button>
                </n-radio-group>
                <p class="field-hint">{{ matcherTypeHint }}</p>
              </n-form-item>
              <n-form-item :label="t('routes.form.matcher.pattern')">
                <n-input v-model:value="form.pattern" :placeholder="t('routes.form.matcher.patternPlaceholder')" />
                <p class="field-hint">{{ patternExample }}</p>
              </n-form-item>
              <n-form-item :label="t('routes.form.matcher.hostPatterns')">
                <n-dynamic-tags v-model:value="form.hostPatterns" />
                <p class="field-hint">{{ t('routes.hints.hostPatterns') }}</p>
              </n-form-item>
              <n-form-item :label="t('routes.form.matcher.methods')">
                <n-dynamic-tags v-model:value="form.methods" />
                <p class="field-hint">{{ t('routes.hints.methods') }}</p>
              </n-form-item>
            </n-card>
          </div>

          <div v-if="currentStep === 2">
          <n-card size="small" :title="t('routes.form.target.title')">
            <n-form-item :label="t('routes.form.type')">
              <n-radio-group v-model:value="form.targetType">
                <n-radio-button v-for="option in targetTypes" :key="option.value" :value="option.value">
                  {{ option.label }}
                </n-radio-button>
              </n-radio-group>
              <p class="field-hint">{{ targetTypeHint }}</p>
            </n-form-item>
            <n-form-item v-if="form.targetType === 'Microservice'" :label="t('routes.form.target.serviceId')">
              <n-input v-model:value="form.serviceId" />
              <p class="field-hint">{{ t('routes.hints.serviceId') }}</p>
            </n-form-item>
            <n-form-item v-if="form.targetType === 'StaticFile'" :label="t('routes.form.target.rootPath')">
              <n-input v-model:value="form.rootPath" />
              <p class="field-hint">{{ t('routes.hints.rootPath') }}</p>
            </n-form-item>
            <n-form-item v-if="form.targetType === 'ExtensionHandler'" :label="t('routes.form.target.handlerId')">
              <n-input v-model:value="form.handlerId" />
              <p class="field-hint">{{ t('routes.hints.handlerId') }}</p>
            </n-form-item>
          </n-card>
          </div>

          <div v-if="currentStep === 3">
            <n-card size="small" :title="t('routes.form.forwarding.title')">
              <n-form-item :label="t('routes.form.forwarding.mode')">
                <n-radio-group v-model:value="form.forwardingMode">
                  <n-radio-button v-for="option in forwardingModes" :key="option.value" :value="option.value">
                    {{ option.label }}
                  </n-radio-button>
                </n-radio-group>
                <p class="field-hint">{{ forwardingModeHint }}</p>
              </n-form-item>
              <n-form-item v-if="form.forwardingMode === 'Replace'" :label="t('routes.form.forwarding.replaceTemplate')">
                <n-input v-model:value="form.replaceTemplate" />
                <p class="field-hint">{{ t('routes.hints.replaceTemplate') }}</p>
              </n-form-item>
              <n-form-item :label="t('routes.form.forwarding.priority')">
                <n-input-number v-model:value="form.priority" :min="0" />
                <p class="field-hint">{{ t('routes.hints.priority') }}</p>
              </n-form-item>
            </n-card>
          </div>
          <div v-if="currentStep === 4">
          <n-collapse>
            <n-collapse-item :title="t('routes.form.advanced.title')" name="advanced">
              <p class="field-hint" style="margin-bottom: 12px">{{ t('routes.hints.rewrites') }}</p>
              <n-form-item :label="t('routes.form.advanced.requestHeaderRewrites')">
                <n-space vertical class="rewrite-list">
                  <n-space v-for="(rewrite, index) in form.requestHeaderRewrites" :key="`request-${index}`" align="center">
                    <n-select v-model:value="rewrite.operation" :options="rewriteOperations" class="rewrite-operation" />
                    <n-input v-model:value="rewrite.name" :placeholder="t('routes.form.advanced.headerNamePlaceholder')" />
                    <n-input v-model:value="rewrite.value" :placeholder="t('routes.form.advanced.valuePlaceholder')" />
                    <n-button type="error" quaternary @click="removeRewrite('requestHeaderRewrites', index)">{{ t('routes.form.advanced.remove') }}</n-button>
                  </n-space>
                  <n-button dashed @click="addRewrite('requestHeaderRewrites')">{{ t('routes.form.advanced.addRequestRewrite') }}</n-button>
                </n-space>
              </n-form-item>
              <n-form-item :label="t('routes.form.advanced.responseHeaderRewrites')">
                <n-space vertical class="rewrite-list">
                  <n-space v-for="(rewrite, index) in form.responseHeaderRewrites" :key="`response-${index}`" align="center">
                    <n-select v-model:value="rewrite.operation" :options="rewriteOperations" class="rewrite-operation" />
                    <n-input v-model:value="rewrite.name" :placeholder="t('routes.form.advanced.headerNamePlaceholder')" />
                    <n-input v-model:value="rewrite.value" :placeholder="t('routes.form.advanced.valuePlaceholder')" />
                    <n-button type="error" quaternary @click="removeRewrite('responseHeaderRewrites', index)">{{ t('routes.form.advanced.remove') }}</n-button>
                  </n-space>
                  <n-button dashed @click="addRewrite('responseHeaderRewrites')">{{ t('routes.form.advanced.addResponseRewrite') }}</n-button>
                </n-space>
              </n-form-item>
              <n-form-item :label="t('routes.form.advanced.metadataJson')">
                <n-input v-model:value="form.metadataJson" type="textarea" :autosize="{ minRows: 3, maxRows: 8 }" />
                <p class="field-hint">{{ t('routes.hints.metadataJson') }}</p>
              </n-form-item>
              <n-grid :cols="2" :x-gap="16">
                <n-form-item :label="t('routes.form.advanced.maxRequestBodyBytes')">
                  <n-input-number v-model:value="form.maxRequestBodyBytes" :min="0" clearable />
                </n-form-item>
                <n-form-item :label="t('routes.form.advanced.maxRequestHeaderBytes')">
                  <n-input-number v-model:value="form.maxRequestHeaderBytes" :min="0" clearable />
                </n-form-item>
                <n-form-item :label="t('routes.form.advanced.maxConcurrentRequests')">
                  <n-input-number v-model:value="form.maxConcurrentRequests" :min="0" clearable />
                </n-form-item>
                <n-form-item :label="t('routes.form.advanced.requestReadTimeoutMs')">
                  <n-input-number v-model:value="form.requestReadTimeoutMs" :min="0" clearable />
                </n-form-item>
              </n-grid>
              <p class="field-hint">{{ t('routes.hints.limits') }}</p>
            </n-collapse-item>
          </n-collapse>
          </div>

          <ApiErrorAlert v-if="saveMutation.isError.value" :error="saveMutation.error.value" />
          <n-alert v-if="formError" type="error" :show-icon="true">{{ formError }}</n-alert>
          <n-space justify="end" class="form-actions">
            <n-button v-if="currentStep > 1" @click="previousStep">{{ t('routes.navigation.previous') }}</n-button>
            <n-button v-if="currentStep < 4" type="primary" @click="nextStep">{{ t('routes.navigation.next') }}</n-button>
            <n-button v-else type="primary" :loading="saveMutation.isPending.value" @click="save">{{ t('common.save') }}</n-button>
            <n-button @click="showForm = false">{{ t('common.cancel') }}</n-button>
          </n-space>
        </n-form>
      </n-card>
    </n-modal>
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
  justify-content: space-between;
}

h1 {
  margin: 0;
}

.page-heading p {
  color: var(--n-text-color-3);
  margin: 6px 0 0;
}

.form-modal {
  display: flex;
  flex-direction: column;
  max-height: 90vh;
  overflow: hidden;
  width: min(900px, calc(100vw - 32px));
}

.form-modal :deep(.n-card-content) {
  overflow-y: auto;
}

.form-modal :deep(.n-form-item-blank) {
  flex-wrap: wrap;
}

.form-steps {
  margin-bottom: 16px;
}

.form-actions {
  margin-top: 16px;
}

.preset-row {
  align-items: center;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 16px;
}

.preset-label {
  color: var(--n-text-color-3);
  font-size: 13px;
}

.field-hint {
  color: var(--n-text-color-3);
  flex: 0 0 100%;
  font-size: 12px;
  margin: 4px 0 0;
}

.rewrite-list {
  width: 100%;
}

.rewrite-operation {
  min-width: 120px;
}
</style>
