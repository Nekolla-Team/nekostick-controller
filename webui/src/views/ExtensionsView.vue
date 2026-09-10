<script setup lang="ts">
import { computed, h, ref, watch } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import {
  NAlert,
  NButton,
  NCard,
  NDataTable,
  NDrawer,
  NDrawerContent,
  NInput,
  NPopconfirm,
  NSpace,
  NSpin,
  NTag,
  useMessage,
} from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import ApiErrorAlert from '../components/ApiErrorAlert.vue'
import {
  deleteExtensionRecord,
  deleteSettings,
  disableExtension,
  enableExtension,
  extensionsPath,
  getSettings,
  listExtensions,
  putSettings,
  refreshExtensions,
  reloadExtension,
} from '../api/resources/extensions'
import type { ExtensionRecord, ExtensionSettings, JsonObject, JsonValue } from '../api/types'
import { useCas } from '../composables/useCas'
import { t } from '../i18n'

const queryClient = useQueryClient()
const cas = useCas(queryClient)
const message = useMessage()
const extensionsQuery = useQuery({ queryKey: ['extensions'], queryFn: listExtensions })
const rows = computed(() => extensionsQuery.data.value ?? [])
const selectedExtensionId = ref<string | null>(null)
const settingsText = ref('{}')
const settingsError = ref<string | null>(null)
const settingsQuery = useQuery({
  queryKey: computed(() => ['extensions', selectedExtensionId.value ?? '', 'settings']),
  queryFn: () => getSettings(selectedExtensionId.value as string),
  enabled: computed(() => selectedExtensionId.value !== null),
})

watch(
  () => settingsQuery.data.value,
  (data: ExtensionSettings | undefined) => {
    if (data === undefined) return
    settingsText.value = JSON.stringify(data.settings, null, 2)
    settingsError.value = null
  },
  { immediate: true },
)

function extensionSettingsPath(id: string): string {
  return `${extensionsPath}/${encodeURIComponent(id)}/settings`
}

function openSettings(extension: ExtensionRecord): void {
  selectedExtensionId.value = extension.extensionId
  settingsError.value = null
}

function closeSettings(): void {
  selectedExtensionId.value = null
  settingsError.value = null
}

function objectDepth(value: JsonValue, depth = 1): number {
  if (typeof value !== 'object' || value === null) return depth
  if (Array.isArray(value)) {
    return Math.max(depth, ...value.map((item) => objectDepth(item, depth + 1)))
  }
  return Math.max(depth, ...Object.values(value).map((item) => objectDepth(item, depth + 1)))
}

function parseSettings(): JsonObject | null {
  try {
    const parsed: unknown = JSON.parse(settingsText.value)
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      settingsError.value = t('extensions.validation.objectRequired')
      return null
    }
    const json = parsed as JsonObject
    if (objectDepth(json) > 32) {
      settingsError.value = t('extensions.validation.maxDepth')
      return null
    }
    return json
  } catch {
    settingsError.value = t('extensions.validation.invalidJson')
    return null
  }
}

const saveMutation = useMutation({
  mutationFn: () => {
    const id = selectedExtensionId.value
    if (!id) throw new Error('No extension selected')
    const parsed = parseSettings()
    if (!parsed) throw new Error(settingsError.value ?? t('extensions.validation.invalid'))
    const schemaVersion = settingsQuery.data.value?.schemaVersion ?? 1
    return cas.run(extensionSettingsPath(id), (ifMatch) => putSettings(id, { schemaVersion, settings: parsed }, ifMatch))
  },
  onSuccess: async () => {
    settingsError.value = null
    const id = selectedExtensionId.value
    if (id) await queryClient.invalidateQueries({ queryKey: ['extensions', id, 'settings'] })
  },
})

const deleteMutation = useMutation({
  mutationFn: () => {
    const id = selectedExtensionId.value
    if (!id) throw new Error('No extension selected')
    return cas.run(extensionSettingsPath(id), (ifMatch) => deleteSettings(id, ifMatch))
  },
  onSuccess: async () => {
    settingsText.value = '{}'
    const id = selectedExtensionId.value
    if (id) await queryClient.invalidateQueries({ queryKey: ['extensions', id, 'settings'] })
  },
})

function save(): void {
  settingsError.value = null
  if (parseSettings() === null) return
  saveMutation.mutate()
}

const lifecycleMutation = useMutation({
  mutationFn: ({ id, action }: { id: string; action: 'enable' | 'disable' | 'reload' | 'deleteRecord' }) => {
    if (action === 'enable') return enableExtension(id)
    if (action === 'disable') return disableExtension(id)
    if (action === 'reload') return reloadExtension(id)
    return deleteExtensionRecord(id)
  },
  onSuccess: async () => {
    await queryClient.invalidateQueries({ queryKey: ['extensions'] })
  },
})

const refreshMutation = useMutation({
  mutationFn: refreshExtensions,
  onSuccess: async (summary) => {
    message.success(t('extensions.refresh.summary', { added: summary.added.length, updated: summary.versionUpdated.length, missing: summary.missing.length, skipped: summary.skipped?.length ?? 0 }))
    await queryClient.invalidateQueries({ queryKey: ['extensions'] })
  },
})

function loadStateTagType(loadState: ExtensionRecord['loadState']): 'success' | 'error' | 'warning' | 'default' {
  if (loadState === 'Loaded') return 'success'
  if (loadState === 'Failed') return 'error'
  if (loadState === 'Disabled') return 'warning'
  return 'default'
}

function displayContentHash(hash: string | null): string {
  if (hash === null) return '—'
  return hash.length > 28 ? `${hash.slice(0, 20)}…${hash.slice(-8)}` : hash
}

const columns = computed<DataTableColumns<ExtensionRecord>>(() => [
  { title: t('extensions.columns.extensionId'), key: 'extensionId' },
  {
    title: t('extensions.columns.version'), key: 'version',
    render: (row) => h(NSpace, { size: 4, align: 'center' }, {
      default: () => [
        h('span', row.version),
        row.manifestVersion !== null && row.manifestVersion !== row.version
          ? h(NTag, { size: 'small', type: 'warning' }, { default: () => t('extensions.columns.manifestDrift', { version: row.manifestVersion ?? '' }) })
          : null,
      ],
    }),
  },
  {
    title: t('extensions.columns.contentHash'), key: 'contentHash',
    render: (row) => h('span', {
      class: 'content-hash',
      title: row.contentHash ?? undefined,
    }, displayContentHash(row.contentHash)),
  },
  {
    title: t('extensions.columns.loadState'), key: 'loadState',
    render: (row) => h(NTag, { type: loadStateTagType(row.loadState) }, { default: () => row.loadState }),
  },
  {
    title: t('extensions.columns.running'), key: 'isRunning', width: 90,
    render: (row) => (row.isRunning ? h(NTag, { size: 'small', type: 'success' }, { default: () => '●' }) : h('span', '—')),
  },
  {
    title: t('extensions.columns.actions'), key: 'actions', width: 320,
    render: (row) => {
      const pending = lifecycleMutation.isPending.value
      const buttons = [
        h(NButton, { size: 'small', disabled: pending, onClick: () => openSettings(row) }, { default: () => t('extensions.columns.editSettings') }),
      ]
      if (row.loadState !== 'Loaded') {
        buttons.push(h(NButton, { size: 'small', type: 'primary', ghost: true, disabled: pending, onClick: () => lifecycleMutation.mutate({ id: row.extensionId, action: 'enable' }) }, { default: () => t('extensions.columns.enable') }))
      }
      if (row.loadState !== 'Disabled') {
        buttons.push(h(NButton, { size: 'small', disabled: pending, onClick: () => lifecycleMutation.mutate({ id: row.extensionId, action: 'disable' }) }, { default: () => t('extensions.columns.disable') }))
      }
      if (row.loadState === 'Loaded') {
        buttons.push(h(NButton, { size: 'small', disabled: pending, onClick: () => lifecycleMutation.mutate({ id: row.extensionId, action: 'reload' }) }, { default: () => t('extensions.columns.reload') }))
      }
      buttons.push(h(NPopconfirm, { onPositiveClick: () => lifecycleMutation.mutate({ id: row.extensionId, action: 'deleteRecord' }) }, {
        trigger: () => h(NButton, { size: 'small', type: 'error', ghost: true, disabled: pending }, { default: () => t('extensions.columns.deleteRecord') }),
        default: () => t('extensions.columns.deleteRecordConfirm'),
      }))
      return h(NSpace, { size: 4 }, { default: () => buttons })
    },
  },
])
</script>

<template>
  <main class="page-stack">
    <header class="page-heading">
      <div>
        <h1>{{ t('extensions.title') }}</h1>
        <p>{{ t('extensions.subtitle') }}</p>
      </div>
      <n-button :loading="refreshMutation.isPending.value" @click="refreshMutation.mutate()">
        {{ t('extensions.refresh.button') }}
      </n-button>
    </header>
    <ApiErrorAlert v-if="extensionsQuery.isError.value" :error="extensionsQuery.error.value" />
    <ApiErrorAlert v-if="lifecycleMutation.isError.value" :error="lifecycleMutation.error.value" />
    <ApiErrorAlert v-if="refreshMutation.isError.value" :error="refreshMutation.error.value" />
    <n-spin :show="extensionsQuery.isLoading.value">
      <n-card>
        <n-data-table :columns="columns" :data="rows" :bordered="false" :single-line="false" />
      </n-card>
    </n-spin>

    <n-drawer :show="selectedExtensionId !== null" :width="640" @update:show="(show) => { if (!show) closeSettings() }">
      <n-drawer-content :title="t('extensions.settings.title')" closable @close="closeSettings">
        <n-spin :show="settingsQuery.isLoading.value">
          <n-space vertical>
            <ApiErrorAlert v-if="settingsQuery.isError.value" :error="settingsQuery.error.value" />
            <ApiErrorAlert v-if="saveMutation.isError.value" :error="saveMutation.error.value" />
            <ApiErrorAlert v-if="deleteMutation.isError.value" :error="deleteMutation.error.value" />
            <n-input v-model:value="settingsText" type="textarea" :autosize="{ minRows: 12, maxRows: 30 }" spellcheck="false" />
            <n-alert v-if="settingsError" type="error" :show-icon="true">{{ settingsError }}</n-alert>
            <n-space justify="end">
              <n-popconfirm :positive-text="t('common.delete')" :negative-text="t('common.cancel')" @positive-click="deleteMutation.mutate()">
                <template #trigger><n-button type="error" secondary>{{ t('extensions.settings.deleteButton') }}</n-button></template>
                {{ t('extensions.settings.deleteConfirm') }}
              </n-popconfirm>
              <n-button type="primary" :loading="saveMutation.isPending.value" @click="save">{{ t('common.save') }}</n-button>
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
.content-hash {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', monospace;
}
</style>
