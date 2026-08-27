<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { useRoute, useRouter } from 'vue-router'
import {
  darkTheme,
  dateEnUS,
  dateZhCN,
  enUS as naiveEnUS,
  zhCN as naiveZhCN,
  NButton,
  NCard,
  NConfigProvider,
  NDialogProvider,
  NDropdown,
  NLayout,
  NLayoutHeader,
  NLayoutContent,
  NMenu,
  NMessageProvider,
  NModal,
  NSpace,
} from 'naive-ui'
import type { DropdownOption, MenuOption } from 'naive-ui'
import BootstrapBanner from './components/BootstrapBanner.vue'
import ControllerConfigModal from './components/ControllerConfigModal.vue'
import ConnectionLostBanner from './components/ConnectionLostBanner.vue'
import { getState } from './api/resources/controller'
import type { ControllerState } from './api/types'
import { connectionLabel, connection } from './stores/connection'
import { isDarkTheme, setThemeMode, theme, type ThemeMode } from './stores/theme'
import { i18nState, setLocale, t, type Locale } from './i18n'

const router = useRouter()
const route = useRoute()
const queryClient = useQueryClient()
const connectionModal = ref(false)
const controllerConfigModal = ref(false)
const controllerStateQuery = useQuery({
  queryKey: ['controller', 'state'],
  queryFn: getState,
  enabled: computed(() => connection.apiKey !== null),
  refetchInterval: 5000,
})
const controllerState = computed<ControllerState | undefined>(() => controllerStateQuery.data.value)
const controllerError = computed(() => controllerStateQuery.error.value)
const controllerFetching = computed(() => controllerStateQuery.isFetching.value)

const appTheme = computed(() => (isDarkTheme.value ? darkTheme : null))
const naiveLocale = computed(() => (i18nState.locale === 'en-US' ? naiveEnUS : naiveZhCN))
const naiveDateLocale = computed(() => (i18nState.locale === 'en-US' ? dateEnUS : dateZhCN))

const themeModeLabelKeys: Record<ThemeMode, string> = {
  light: 'app.theme.light',
  dark: 'app.theme.dark',
  system: 'app.theme.system',
}
const themeOptions = computed<DropdownOption[]>(() =>
  (Object.keys(themeModeLabelKeys) as ThemeMode[]).map((mode) => ({
    label: theme.mode === mode ? `✓ ${t(themeModeLabelKeys[mode])}` : t(themeModeLabelKeys[mode]),
    key: mode,
  })),
)

const languageLabels: Record<Locale, string> = {
  'zh-CN': '简体中文',
  'en-US': 'English',
}
const languageOptions = computed<DropdownOption[]>(() =>
  (Object.keys(languageLabels) as Locale[]).map((value) => ({
    label: i18nState.locale === value ? `✓ ${languageLabels[value]}` : languageLabels[value],
    key: value,
  })),
)

watch(
  () => controllerStateQuery.status.value,
  (status, previousStatus) => {
    if (previousStatus === 'error' && status === 'success') {
      void queryClient.invalidateQueries()
    }
  },
)

const menuOptions = computed<MenuOption[]>(() => [
  { label: t('app.menu.dashboard'), key: '/' },
  { label: t('app.menu.routes'), key: '/routes' },
  { label: t('app.menu.services'), key: '/services' },
  { label: t('app.menu.extensions'), key: '/extensions' },
  { label: t('app.menu.globalSettings'), key: '/global-settings' },
  { label: t('app.menu.connect'), key: '/connect' },
])

const selectedMenu = computed(() => {
  if (route.path.startsWith('/services/')) return '/services'
  return route.path
})
const displayedConnectionLabel = connectionLabel

function navigate(key: string): void {
  void router.push(key)
}

function handleThemeSelect(key: string | number): void {
  if (key === 'light' || key === 'dark' || key === 'system') {
    setThemeMode(key)
  }
}

function handleLanguageSelect(key: string | number): void {
  if (key === 'zh-CN' || key === 'en-US') {
    setLocale(key)
  }
}

function openConnectionSettings(): void {
  connectionModal.value = true
}

function goToConnection(): void {
  connectionModal.value = false
  void router.push('/connect')
}
function openControllerConfig(): void {
  connectionModal.value = false
  controllerConfigModal.value = true
}

async function retryControllerState(): Promise<void> {
  await controllerStateQuery.refetch()
}
</script>

<template>
  <n-config-provider
    :theme="appTheme"
    :locale="naiveLocale"
    :date-locale="naiveDateLocale"
  >
    <n-message-provider>
      <n-dialog-provider>
        <n-layout class="app-shell">
          <n-layout-header bordered class="app-header">
            <div class="app-title">Nekostick Controller</div>
            <n-menu
              mode="horizontal"
              :value="selectedMenu"
              :options="menuOptions"
              @update:value="navigate"
            />
            <n-button text class="connection-button" @click="openConnectionSettings">
              {{ displayedConnectionLabel }}
            </n-button>
            <n-dropdown trigger="click" :options="themeOptions" @select="handleThemeSelect">
              <n-button text class="theme-button">
                {{ t('app.theme.label') }}: {{ t(themeModeLabelKeys[theme.mode]) }}
              </n-button>
            </n-dropdown>
            <n-dropdown trigger="click" :options="languageOptions" @select="handleLanguageSelect">
              <n-button text class="language-button">
                {{ t('app.language.label') }}: {{ languageLabels[i18nState.locale] }}
              </n-button>
            </n-dropdown>
          </n-layout-header>
          <n-layout-content class="app-content">
            <n-space vertical size="small">
              <BootstrapBanner :state="controllerState" @edit-config="openControllerConfig" />
              <ConnectionLostBanner
                :error="controllerError"
                :fetching="controllerFetching"
                @retry="retryControllerState"
              />
              <router-view />
            </n-space>
          </n-layout-content>
        </n-layout>
        <n-modal v-model:show="connectionModal">
          <n-card
            class="connection-modal-card"
            :title="t('app.connection.modalTitle')"
            closable
            @close="connectionModal = false"
          >
            <p>{{ t('app.connection.address', { label: displayedConnectionLabel }) }}</p>
            <p class="connection-hint">{{ t('app.connection.hint') }}</p>
            <n-space>
              <n-button type="primary" @click="goToConnection">{{ t('app.connection.open') }}</n-button>
              <n-button @click="openControllerConfig">{{ t('app.connection.editControllerConfig') }}</n-button>
            </n-space>
          </n-card>
        </n-modal>
        <ControllerConfigModal v-model:show="controllerConfigModal" />
      </n-dialog-provider>
    </n-message-provider>
  </n-config-provider>
</template>

<style>
body {
  margin: 0;
}
</style>

<style scoped>
.app-shell {
  height: 100vh;
}

.app-shell > :deep(.n-layout-scroll-container) {
  height: 100%;
  overflow: visible;
  display: flex;
  flex-direction: column;
}

.app-content {
  flex: 1 1 auto;
  min-height: 0;
}

.app-content :deep(.n-layout-scroll-container) {
  height: 100%;
  overflow-y: auto;
  padding: 24px;
}

.app-header {
  align-items: center;
  display: flex;
  gap: 24px;
  padding: 0 24px;
}

.app-title {
  flex: 0 0 auto;
  font-size: 1.1rem;
  font-weight: 700;
  white-space: nowrap;
}

.app-header :deep(.n-menu) {
  flex: 1;
}

.connection-button {
  flex: 0 0 auto;
  max-width: 280px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.theme-button,
.language-button {
  flex: 0 0 auto;
  white-space: nowrap;
}

.app-content {
  padding: 0;
}

.connection-modal-card p {
  margin: 0;
}

.connection-modal-card .connection-hint {
  color: var(--n-text-color-3);
  margin: 8px 0 24px;
}

.connection-modal-card :deep(.n-card-content) {
  padding-bottom: 24px;
}

.connection-modal-card {
  box-sizing: border-box;
  max-width: calc(100vw - 32px);
  width: 480px;
}

@media (max-width: 800px) {
  .app-header {
    align-items: flex-start;
    flex-direction: column;
    gap: 8px;
    padding: 12px 16px;
  }

  .app-header :deep(.n-menu) {
    max-width: 100%;
    overflow-x: auto;
  }

  .app-content {
    padding: 16px;
  }
}
</style>
