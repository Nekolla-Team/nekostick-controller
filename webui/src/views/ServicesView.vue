<script setup lang="ts">
import { computed, h, reactive, ref, watch } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { useRouter } from 'vue-router'
import {
  NAlert,
  NButton,
  NCard,
  NDataTable,
  NDrawer,
  NDrawerContent,
  NDynamicTags,
  NForm,
  NFormItem,
  NInput,
  NInputNumber,
  NModal,
  NPopconfirm,
  NRadioButton,
  NRadioGroup,
  NSpace,
  NSpin,
  NSwitch,
  NTag,
} from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import ApiErrorAlert from '../components/ApiErrorAlert.vue'
import {
  createService,
  deleteEnvironment,
  deleteService,
  getEnvironment,
  listServices,
  patchService,
  putEnvironment,
  servicesPath,
} from '../api/resources/services'
import type {
  HealthCheckType,
  HealthCheckWrite,
  ServiceCreateBody,
  ServiceDto,
  ServicePatchBody,
  ServiceRestartPolicy,
  ServiceStartMode,
} from '../api/types'
import { useCas } from '../composables/useCas'
import { t } from '../i18n'

interface ServiceForm {
  enabled: boolean
  fileName: string
  argumentList: string[]
  workingDirectory: string
  startMode: ServiceStartMode
  restartPolicy: ServiceRestartPolicy
  healthType: HealthCheckType
  httpPath: string
  timeoutMs: number | null
}
interface EnvironmentRow {
  key: string
  value: string
  revealed: boolean
}
const router = useRouter()

const queryClient = useQueryClient()
const cas = useCas(queryClient)
const servicesQuery = useQuery({ queryKey: ['services'], queryFn: listServices })
const rows = computed(() => servicesQuery.data.value ?? [])
const showForm = ref(false)
const editingId = ref<string | null>(null)
const formError = ref<string | null>(null)
const form = reactive<ServiceForm>(blankForm())
const environmentServiceId = ref<string | null>(null)
const environmentRows = ref<EnvironmentRow[]>([])
const environmentError = ref<string | null>(null)

function blankForm(): ServiceForm {
  return {
    enabled: true,
    fileName: '',
    argumentList: [],
    workingDirectory: '',
    startMode: 'Lazy',
    restartPolicy: 'OnFailure',
    healthType: 'Process',
    httpPath: '/healthz',
    timeoutMs: 5000,
  }
}

function servicePath(id: string): string {
  return `${servicesPath}/${encodeURIComponent(id)}`
}

function formFromService(service: ServiceDto): ServiceForm {
  return {
    enabled: service.enabled,
    fileName: service.fileName,
    argumentList: [...service.argumentList],
    workingDirectory: service.workingDirectory,
    startMode: service.startMode,
    restartPolicy: service.restartPolicy,
    healthType: service.healthCheck.type,
    httpPath: service.healthCheck.httpPath ?? '/healthz',
    timeoutMs: service.healthCheck.timeoutMs,
  }
}

function buildHealthCheck(): HealthCheckWrite {
  if (form.healthType === 'Http') {
    return { type: 'Http', httpPath: form.httpPath.trim() || '/', timeoutMs: form.timeoutMs ?? 0 }
  }
  if (form.healthType === 'Tcp') {
    return { type: 'Tcp', timeoutMs: form.timeoutMs ?? 0 }
  }
  return { type: 'Process', timeoutMs: form.timeoutMs ?? 0 }
}

function buildBody(): ServiceCreateBody {
  return {
    enabled: form.enabled,
    fileName: form.fileName.trim(),
    argumentList: [...form.argumentList],
    workingDirectory: form.workingDirectory.trim(),
    startMode: form.startMode,
    restartPolicy: form.restartPolicy,
    healthCheck: buildHealthCheck(),
  }
}

function resetForm(): void {
  Object.assign(form, blankForm())
  formError.value = null
}

function openCreate(): void {
  editingId.value = null
  resetForm()
  showForm.value = true
}

function openEdit(service: ServiceDto): void {
  editingId.value = service.id
  Object.assign(form, formFromService(service))
  formError.value = null
  showForm.value = true
}

const saveMutation = useMutation({
  mutationFn: () => {
    const body = buildBody()
    if (editingId.value) {
      const id = editingId.value
      return cas.run(servicePath(id), (ifMatch) => patchService(id, body as ServicePatchBody, ifMatch))
    }
    return cas.run(servicesPath, (ifMatch) => createService(body, ifMatch))
  },
  onSuccess: async () => {
    showForm.value = false
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['services'] }),
      queryClient.invalidateQueries({ queryKey: ['root'] }),
    ])
  },
})

const toggleMutation = useMutation({
  mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
    cas.run(servicePath(id), (ifMatch) => patchService(id, { enabled }, ifMatch)),
  onSuccess: async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['services'] }),
      queryClient.invalidateQueries({ queryKey: ['root'] }),
    ])
  },
})

const deleteMutation = useMutation({
  mutationFn: (id: string) => cas.run(servicePath(id), (ifMatch) => deleteService(id, ifMatch)),
  onSuccess: async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['services'] }),
      queryClient.invalidateQueries({ queryKey: ['root'] }),
    ])
  },
})

function save(): void {
  formError.value = null
  if (!form.fileName.trim()) {
    formError.value = t('services.validation.fileNameRequired')
    return
  }
  saveMutation.mutate()
}

const environmentQuery = useQuery({
  queryKey: computed(() => ['services', environmentServiceId.value ?? '', 'environment']),
  queryFn: () => getEnvironment(environmentServiceId.value as string),
  enabled: computed(() => environmentServiceId.value !== null),
})

watch(
  () => environmentQuery.data.value,
  (data) => {
    if (!data) return
    environmentRows.value = Object.entries(data.environment).map(([key, value]) => ({
      key,
      value,
      revealed: false,
    }))
  },
  { immediate: true },
)

function openEnvironment(service: ServiceDto): void {
  environmentError.value = null
  environmentServiceId.value = service.id
}

function closeEnvironment(): void {
  environmentServiceId.value = null
  environmentRows.value = []
  environmentError.value = null
}

function addEnvironmentRow(): void {
  environmentRows.value.push({ key: '', value: '', revealed: false })
}

function removeEnvironmentRow(index: number): void {
  environmentRows.value.splice(index, 1)
}

function environmentMap(): Record<string, string> {
  return environmentRows.value.reduce<Record<string, string>>((result, row) => {
    const key = row.key.trim()
    if (key) result[key] = row.value
    return result
  }, {})
}

const saveEnvironmentMutation = useMutation({
  mutationFn: () => {
    const id = environmentServiceId.value
    if (!id) throw new Error(t('services.errors.noServiceSelected'))
    return cas.run(`${servicePath(id)}/environment`, (ifMatch) => putEnvironment(id, environmentMap(), ifMatch))
  },
  onSuccess: async () => {
    await queryClient.invalidateQueries({ queryKey: ['services', environmentServiceId.value, 'environment'] })
    environmentError.value = null
  },
  onError: (error: unknown) => { environmentError.value = error instanceof Error ? error.message : t('services.errors.saveEnvironmentFallback') },
})

const clearEnvironmentMutation = useMutation({
  mutationFn: () => {
    const id = environmentServiceId.value
    if (!id) throw new Error(t('services.errors.noServiceSelected'))
    return cas.run(`${servicePath(id)}/environment`, (ifMatch) => deleteEnvironment(id, ifMatch))
  },
  onSuccess: async () => {
    environmentRows.value = []
    await queryClient.invalidateQueries({ queryKey: ['services', environmentServiceId.value, 'environment'] })
    environmentError.value = null
  },
  onError: (error: unknown) => { environmentError.value = error instanceof Error ? error.message : t('services.errors.clearEnvironmentFallback') },
})

function localeEnumKey(value: string): string {
  return `${value.charAt(0).toLowerCase()}${value.slice(1)}`
}

function healthText(service: ServiceDto): string {
  return t(`services.enums.healthTypes.${localeEnumKey(service.healthCheck.type)}`)
}

const columns = computed<DataTableColumns<ServiceDto>>(() => [
  { title: t('services.columns.file'), key: 'fileName' },
  {
    title: t('common.enabled'), key: 'enabled', width: 80,
    render: (row) => h(NSwitch, {
      value: row.enabled,
      loading: toggleMutation.isPending.value,
      'onUpdate:value': (enabled: boolean) => toggleMutation.mutate({ id: row.id, enabled }),
    }),
  },
  {
    title: t('services.columns.startMode'),
    key: 'startMode',
    render: (row) => h(NTag, { type: row.startMode === 'Eager' ? 'success' : 'default' }, {
      default: () => t(`services.enums.startModes.${localeEnumKey(row.startMode)}`),
    }),
  },
  {
    title: t('services.columns.restartPolicy'),
    key: 'restartPolicy',
    render: (row) => t(`services.enums.restartPolicies.${localeEnumKey(row.restartPolicy)}`),
  },
  { title: t('services.columns.healthCheck'), key: 'healthCheck', render: healthText },
  {
    title: t('common.actions'), key: 'actions', width: 260,
    render: (row) => h(NSpace, { size: 'small' }, {
      default: () => [
        h(NButton, { size: 'small', onClick: () => openEdit(row) }, { default: () => t('common.edit') }),
        h(NButton, { size: 'small', onClick: () => openEnvironment(row) }, { default: () => t('services.actions.environmentVariables') }),
        h(NButton, { size: 'small', onClick: () => void router.push(`/services/${encodeURIComponent(row.id)}/runtime`) }, { default: () => t('services.actions.runtimeStatus') }),
        h(NPopconfirm, {
          positiveText: t('common.delete'), negativeText: t('common.cancel'), onPositiveClick: () => deleteMutation.mutate(row.id),
        }, {
          trigger: () => h(NButton, { size: 'small', type: 'error', loading: deleteMutation.isPending.value }, { default: () => t('common.delete') }),
          default: () => t('services.confirm.deleteService'),
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
        <h1>{{ t('services.title') }}</h1>
        <p>{{ t('services.subtitle') }}</p>
      </div>
      <n-button type="primary" @click="openCreate">{{ t('services.create') }}</n-button>
    </header>

    <ApiErrorAlert v-if="servicesQuery.isError" :error="servicesQuery.error" />
    <ApiErrorAlert v-if="toggleMutation.isError" :error="toggleMutation.error" />
    <ApiErrorAlert v-if="deleteMutation.isError" :error="deleteMutation.error" />
    <n-spin :show="servicesQuery.isLoading.value">
      <n-card>
        <n-data-table :columns="columns" :data="rows" :bordered="false" :single-line="false" />
      </n-card>
    </n-spin>

    <n-modal v-model:show="showForm">
      <n-card class="form-modal" :title="editingId ? t('services.modal.edit') : t('services.modal.create')" closable @close="showForm = false">
        <n-form @submit.prevent="save">
          <n-form-item :label="t('common.enabled')"><n-switch v-model:value="form.enabled" /></n-form-item>
          <n-form-item :label="t('services.form.fileName')"><n-input v-model:value="form.fileName" /></n-form-item>
          <n-form-item :label="t('services.form.argumentList')"><n-dynamic-tags v-model:value="form.argumentList" /></n-form-item>
          <n-form-item :label="t('services.form.workingDirectory')"><n-input v-model:value="form.workingDirectory" /></n-form-item>
          <n-form-item :label="t('services.form.startMode')">
            <n-radio-group v-model:value="form.startMode">
              <n-radio-button value="Eager">{{ t('services.enums.startModes.eager') }}</n-radio-button>
              <n-radio-button value="Lazy">{{ t('services.enums.startModes.lazy') }}</n-radio-button>
            </n-radio-group>
          </n-form-item>
          <n-form-item :label="t('services.form.restartPolicy')">
            <n-radio-group v-model:value="form.restartPolicy">
              <n-radio-button value="Never">{{ t('services.enums.restartPolicies.never') }}</n-radio-button>
              <n-radio-button value="OnFailure">{{ t('services.enums.restartPolicies.onFailure') }}</n-radio-button>
              <n-radio-button value="Always">{{ t('services.enums.restartPolicies.always') }}</n-radio-button>
            </n-radio-group>
          </n-form-item>
          <n-form-item :label="t('services.form.healthCheck')">
            <n-radio-group v-model:value="form.healthType">
              <n-radio-button value="Process">{{ t('services.form.healthTypeOptions.process') }}</n-radio-button>
              <n-radio-button value="Tcp">{{ t('services.form.healthTypeOptions.tcp') }}</n-radio-button>
              <n-radio-button value="Http">{{ t('services.form.healthTypeOptions.http') }}</n-radio-button>
            </n-radio-group>
          </n-form-item>
          <n-form-item v-if="form.healthType === 'Http'" :label="t('services.form.httpPath')"><n-input v-model:value="form.httpPath" /></n-form-item>
          <n-form-item :label="t('services.form.timeout')"><n-input-number v-model:value="form.timeoutMs" :min="0" /></n-form-item>
          <n-alert v-if="formError" type="error" :show-icon="true">{{ formError }}</n-alert>
          <n-space justify="end">
            <n-button @click="showForm = false">{{ t('common.cancel') }}</n-button>
            <n-button type="primary" attr-type="submit" :loading="saveMutation.isPending.value">{{ t('common.save') }}</n-button>
          </n-space>
        </n-form>
      </n-card>
    </n-modal>

    <n-drawer :show="environmentServiceId !== null" :width="560" @update:show="(show) => { if (!show) closeEnvironment() }">
      <n-drawer-content :title="t('services.environment.title')" closable @close="closeEnvironment">
        <n-spin :show="environmentQuery.isLoading.value">
          <ApiErrorAlert v-if="environmentQuery.isError" :error="environmentQuery.error" />
          <ApiErrorAlert v-if="saveEnvironmentMutation.isError" :error="saveEnvironmentMutation.error" />
          <ApiErrorAlert v-if="clearEnvironmentMutation.isError" :error="clearEnvironmentMutation.error" />
          <n-alert v-if="environmentError" type="error" :show-icon="true">{{ environmentError }}</n-alert>
          <n-space vertical>
            <n-space v-for="(row, index) in environmentRows" :key="index" align="center" :wrap="false">
              <n-input v-model:value="row.key" :placeholder="t('services.environment.keyPlaceholder')" />
              <n-input
                v-model:value="row.value"
                :type="row.revealed ? 'text' : 'password'"
                show-password-on="click"
                :placeholder="t('services.environment.valuePlaceholder')"
              />
              <n-button type="error" quaternary @click="removeEnvironmentRow(index)">{{ t('services.environment.remove') }}</n-button>
            </n-space>
            <n-button dashed @click="addEnvironmentRow">{{ t('services.environment.add') }}</n-button>
            <n-space justify="end">
              <n-popconfirm
                :positive-text="t('services.environment.clear')"
                :negative-text="t('common.cancel')"
                @positive-click="clearEnvironmentMutation.mutate()"
              >
                <template #trigger><n-button type="error" secondary>{{ t('services.environment.clear') }}</n-button></template>
                {{ t('services.confirm.clearEnvironment') }}
              </n-popconfirm>
              <n-button type="primary" :loading="saveEnvironmentMutation.isPending.value" @click="saveEnvironmentMutation.mutate()">{{ t('common.save') }}</n-button>
            </n-space>
          </n-space>
        </n-spin>
      </n-drawer-content>
    </n-drawer>
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
  max-height: 90vh;
  overflow: auto;
  width: min(640px, calc(100vw - 32px));
}
</style>
