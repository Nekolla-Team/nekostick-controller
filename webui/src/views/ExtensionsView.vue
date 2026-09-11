<script setup lang="ts">
import { computed, h, onBeforeUnmount, ref, watch } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import {
  NAlert,
  NButton,
  NCard,
  NDataTable,
  NDrawer,
  NDrawerContent,
  NInput,
  NModal,
  NPopconfirm,
  NSpace,
  NSpin,
  NTag,
  useMessage,
} from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import ApiErrorAlert from '../components/ApiErrorAlert.vue'
import {
  cancelExtensionPackageUpload,
  deleteExtensionRecord,
  deleteSettings,
  disableExtension,
  enableExtension,
  extensionsPath,
  getSettings,
  installExtensionPackage,
  listExtensions,
  putSettings,
  refreshExtensions,
  reloadExtension,
} from '../api/resources/extensions'
import { ApiClientError } from '../api/client'
import type { ExtensionInstallResult, ExtensionRecord, ExtensionSettings, JsonObject, JsonValue } from '../api/types'
import { useCas } from '../composables/useCas'
import { t } from '../i18n'

type UploadEntryStatus = 'pending' | 'uploading' | 'done' | 'failed'

interface UploadEntry {
  id: number
  file: File
  name: string
  size: number
  status: UploadEntryStatus
  progress: number | null
  result: ExtensionInstallResult | null
  error: unknown
}

const queryClient = useQueryClient()
const cas = useCas(queryClient)
const message = useMessage()
const extensionsQuery = useQuery({ queryKey: ['extensions'], queryFn: listExtensions })
const rows = computed(() => extensionsQuery.data.value ?? [])
const showUploadModal = ref(false)
const uploadEntries = ref<UploadEntry[]>([])
const fileInput = ref<HTMLInputElement | null>(null)
const uploadQueueRunning = ref(false)
let nextUploadId = 0
let uploadGeneration = 0
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
function formatFileSize(size: number): string {
  const units = ['B', 'KiB', 'MiB', 'GiB']
  let value = size
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }
  const formatted = unit === 0 || value >= 10 ? Math.round(value).toString() : value.toFixed(1)
  return `${formatted} ${units[unit]}`
}

function uploadFailureMessage(entry: UploadEntry): string {
  if (entry.error instanceof ApiClientError) {
    // The install endpoint reports the concrete rejection reason in the envelope message;
    // prefer it over the generic per-kind text so users see what to fix.
    const reported = entry.error.message?.trim()
    if (reported) return reported
    if (entry.error.code === 'downgrade_forbidden') return t('extensions.install.downgradeForbidden')
    if (entry.error.kind !== 'network') {
      const key = `errors.byKind.${entry.error.kind}`
      const localized = t(key)
      if (localized !== key) return localized
    }
  }
  return t('extensions.install.genericFailure')
}

function openUploadModal(): void {
  if (!uploadQueueRunning.value && uploadEntries.value.every((entry) => entry.status === 'done' || entry.status === 'failed')) {
    uploadEntries.value = []
  }
  showUploadModal.value = true
  void drainUploadQueue()
}

function closeUploadModal(): void {
  uploadGeneration += 1
  cancelExtensionPackageUpload()
  showUploadModal.value = false
}

function updateUploadModalVisibility(show: boolean): void {
  if (show) {
    openUploadModal()
  } else {
    closeUploadModal()
  }
}

function appendUploadFiles(files: File[]): void {
  if (files.length === 0) return
  uploadEntries.value.push(...files.map((file) => ({
    id: nextUploadId++,
    file,
    name: file.name,
    size: file.size,
    status: 'pending' as const,
    progress: 0,
    result: null,
    error: null,
  })))
  void drainUploadQueue()
}

function openFilePicker(): void {
  fileInput.value?.click()
}

function onFileInputChange(event: Event): void {
  const input = event.target as HTMLInputElement
  appendUploadFiles(input.files ? Array.from(input.files) : [])
  input.value = ''
}

function onDrop(event: DragEvent): void {
  appendUploadFiles(event.dataTransfer ? Array.from(event.dataTransfer.files) : [])
}

function onDropZoneKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Enter' && event.key !== ' ') return
  event.preventDefault()
  openFilePicker()
}

async function drainUploadQueue(): Promise<void> {
  if (uploadQueueRunning.value) return
  uploadQueueRunning.value = true
  const generation = uploadGeneration
  let batchHadSuccess = false

  try {
    while (generation === uploadGeneration && showUploadModal.value) {
      const entry = uploadEntries.value.find((candidate) => candidate.status === 'pending')
      if (!entry) break

      entry.status = 'uploading'
      entry.progress = 0
      entry.error = null
      try {
        const result = await installExtensionPackage(entry.file, (fraction) => {
          if (generation !== uploadGeneration || entry.status !== 'uploading') return
          entry.progress = Number.isFinite(fraction) ? Math.min(1, Math.max(0, fraction)) : null
        })
        entry.status = 'done'
        entry.progress = 1
        entry.result = result
        if (generation === uploadGeneration) batchHadSuccess = true
      } catch (error: unknown) {
        entry.status = 'failed'
        entry.progress = null
        entry.error = error
      }
    }

    if (generation === uploadGeneration && showUploadModal.value && batchHadSuccess && !uploadEntries.value.some((entry) => entry.status === 'pending')) {
      await queryClient.invalidateQueries({ queryKey: ['extensions'] })
    }
  } finally {
    uploadQueueRunning.value = false
    if (showUploadModal.value && uploadEntries.value.some((entry) => entry.status === 'pending')) {
      void drainUploadQueue()
    }
  }
}

onBeforeUnmount(closeUploadModal)

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
      <n-space>
        <n-button type="primary" @click="openUploadModal">
          {{ t('extensions.install.button') }}
        </n-button>
        <n-button :loading="refreshMutation.isPending.value" @click="refreshMutation.mutate()">
          {{ t('extensions.refresh.button') }}
        </n-button>
      </n-space>
    </header>
    <ApiErrorAlert v-if="extensionsQuery.isError.value" :error="extensionsQuery.error.value" />
    <ApiErrorAlert v-if="lifecycleMutation.isError.value" :error="lifecycleMutation.error.value" />
    <ApiErrorAlert v-if="refreshMutation.isError.value" :error="refreshMutation.error.value" />
    <n-spin :show="extensionsQuery.isLoading.value">
      <n-card>
        <n-data-table :columns="columns" :data="rows" :bordered="false" :single-line="false" />
      </n-card>
    </n-spin>
    <n-modal :show="showUploadModal" @update:show="updateUploadModalVisibility">
      <n-card class="upload-modal" :title="t('extensions.install.title')" closable @close="closeUploadModal">
        <div class="upload-modal-content">
          <div class="upload-list">
            <div v-for="entry in uploadEntries" :key="entry.id" class="upload-entry">
              <div class="upload-entry-heading">
                <span class="upload-entry-name" :title="entry.name">{{ entry.name }}</span>
                <span class="upload-entry-size">{{ formatFileSize(entry.size) }}</span>
              </div>
              <progress
                v-if="entry.status === 'pending' || entry.status === 'uploading'"
                class="upload-progress"
                max="1"
                :value="entry.progress === null ? undefined : entry.progress"
              />
              <div class="upload-entry-state">
                <template v-if="entry.status === 'done'">
                  <span>{{ t('extensions.install.done', { id: entry.result?.id ?? '', version: entry.result?.version ?? '' }) }}</span>
                  <n-tag v-if="entry.result?.replaced" size="small" type="success">
                    {{ t('extensions.install.replaced') }}
                  </n-tag>
                </template>
                <span v-else-if="entry.status === 'failed'" class="upload-error">{{ t('extensions.install.failed') }}: {{ uploadFailureMessage(entry) }}</span>
                <span v-else-if="entry.status === 'uploading'">{{ t('extensions.install.uploading') }}</span>
                <span v-else>{{ t('extensions.install.pending') }}</span>
              </div>
            </div>
          </div>
          <div
            class="upload-dropzone"
            role="button"
            tabindex="0"
            @click="openFilePicker"
            @keydown="onDropZoneKeydown"
            @dragover.prevent
            @drop.prevent="onDrop"
          >
            <input
              ref="fileInput"
              class="upload-file-input"
              type="file"
              accept=".zip,application/zip"
              multiple
              @change="onFileInputChange"
              @click.stop
            />
            <span>{{ t('extensions.install.dropHint') }}</span>
          </div>
        </div>
      </n-card>
    </n-modal>

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
.upload-modal {
  display: flex;
  flex-direction: column;
  max-height: 90vh;
  overflow: hidden;
  width: min(680px, calc(100vw - 32px));
}

.upload-modal :deep(.n-card-content) {
  overflow-y: auto;
}

.upload-modal-content {
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-height: 0;
}

.upload-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
  max-height: 45vh;
  overflow-y: auto;
}


.upload-entry {
  border: 1px solid var(--n-border-color);
  border-radius: 6px;
  padding: 10px 12px;
}

.upload-entry-heading,
.upload-entry-state {
  align-items: center;
  display: flex;
  gap: 8px;
}

.upload-entry-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.upload-entry-size {
  color: var(--n-text-color-3);
  flex: 0 0 auto;
  font-size: 12px;
}

.upload-progress {
  display: block;
  height: 8px;
  margin-top: 8px;
  width: 100%;
}

.upload-entry-state {
  color: var(--n-text-color-3);
  flex-wrap: wrap;
  font-size: 13px;
  margin-top: 6px;
  min-height: 20px;
}

.upload-error {
  color: var(--n-error-color);
}

.upload-dropzone {
  align-items: center;
  border: 1px dashed var(--n-border-color);
  border-radius: 6px;
  color: var(--n-text-color-3);
  cursor: pointer;
  display: flex;
  justify-content: center;
  min-height: 96px;
  padding: 16px;
  text-align: center;
  transition: border-color 0.2s ease, color 0.2s ease;
}

.upload-dropzone:hover,
.upload-dropzone:focus-visible {
  border-color: var(--n-primary-color);
  color: var(--n-primary-color);
  outline: none;
}

.upload-file-input {
  height: 1px;
  opacity: 0;
  pointer-events: none;
  position: absolute;
  width: 1px;
}

.content-hash {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', monospace;
}
</style>
