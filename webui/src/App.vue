<script setup lang="ts">
import { computed, h, ref, watch, type Component } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { useRoute, useRouter } from 'vue-router'
import {
  darkTheme,
  dateEnUS,
  dateZhCN,
  enUS as naiveEnUS,
  zhCN as naiveZhCN,
  NButton,
  NConfigProvider,
  NDialogProvider,
  NDrawer,
  NDrawerContent,
  NDropdown,
  NIcon,
  NLayout,
  NLayoutHeader,
  NLayoutContent,
  NLayoutSider,
  NMenu,
  NMessageProvider,
  NSpace,
} from 'naive-ui'
import type { DropdownOption, GlobalThemeOverrides, MenuOption } from 'naive-ui'
import BootstrapBanner from './components/BootstrapBanner.vue'
import ConnectionLostBanner from './components/ConnectionLostBanner.vue'
import {
  IconCollapse,
  IconConnect,
  IconDashboard,
  IconExpand,
  IconExtension,
  IconGlobe,
  IconMenu,
  IconMoon,
  IconCatHead,
  IconRoute,
  IconService,
  IconSettings,
  IconSun,
  IconSystem,
} from './components/icons'
import { getState, isReloadWindowError } from './api/resources/controller'
import type { ControllerState } from './api/types'
import { usePersistentError } from './composables/usePersistentError'
import { connection } from './stores/connection'
import { isDarkTheme, setThemeMode, theme, type ThemeMode } from './stores/theme'
import { i18nState, setLocale, t, type Locale } from './i18n'

const router = useRouter()
const route = useRoute()
const queryClient = useQueryClient()
const mobileDrawer = ref(false)
const siderCollapsed = ref(false)
const controllerStateQuery = useQuery({
  queryKey: ['controller', 'state'],
  queryFn: getState,
  enabled: computed(() => connection.apiKey !== null && route.meta.bare !== true),
  refetchInterval: 5000,
})
const controllerState = computed<ControllerState | undefined>(() => controllerStateQuery.data.value)
const controllerFetching = computed(() => controllerStateQuery.isFetching.value)
// Reload-induced drops are silent (a reload recycles the transport under the poller), and any
// other failure has to repeat before it shows, so the banner does not flicker each poll cycle.
const controllerError = usePersistentError(controllerStateQuery, isReloadWindowError).error

const isBare = computed(() => route.meta.bare === true)
const appTheme = computed(() => (isDarkTheme.value ? darkTheme : null))

const fontFamily = "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, 'PingFang SC', 'Hiragino Sans GB', 'Microsoft YaHei', sans-serif"

// Brand: sakura pink. Dark mode keeps the pastel tone with dark ink on it; light mode
// deepens to a rose so white ink stays readable.
const themeOverrides = computed<GlobalThemeOverrides>(() =>
  isDarkTheme.value
    ? {
        common: {
          primaryColor: '#f2a7c8',
          primaryColorHover: '#f7c3da',
          primaryColorPressed: '#e889b1',
          primaryColorSuppl: '#f2a7c8',
          borderRadius: '8px',
          borderRadiusSmall: '6px',
          fontFamily,
          bodyColor: '#0f0f14',
          cardColor: '#17171e',
          modalColor: '#1b1b24',
          popoverColor: '#1f1f29',
          borderColor: 'rgba(255, 255, 255, 0.09)',
        },
        Card: { borderRadius: '12px' },
        Menu: { borderRadius: '8px' },
      }
    : {
        common: {
          primaryColor: '#c2255c',
          primaryColorHover: '#d6447a',
          primaryColorPressed: '#a61e4d',
          primaryColorSuppl: '#c2255c',
          borderRadius: '8px',
          borderRadiusSmall: '6px',
          fontFamily,
          bodyColor: '#f5f5fa',
          cardColor: '#ffffff',
          borderColor: 'rgba(0, 0, 0, 0.09)',
        },
        Card: { borderRadius: '12px' },
        Menu: { borderRadius: '8px' },
      },
)
const naiveLocale = computed(() => (i18nState.locale === 'en-US' ? naiveEnUS : naiveZhCN))
const naiveDateLocale = computed(() => (i18nState.locale === 'en-US' ? dateEnUS : dateZhCN))

const isMobile = ref(false)
if (typeof window !== 'undefined') {
  const mobileMedia = window.matchMedia('(max-width: 899px)')
  isMobile.value = mobileMedia.matches
  mobileMedia.addEventListener('change', (event) => {
    isMobile.value = event.matches
    if (!event.matches) mobileDrawer.value = false
  })
}

const themeModeLabelKeys: Record<ThemeMode, string> = {
  light: 'app.theme.light',
  dark: 'app.theme.dark',
  system: 'app.theme.system',
}
const themeIcons: Record<ThemeMode, Component> = {
  light: IconSun,
  dark: IconMoon,
  system: IconSystem,
}
const themeIcon = computed(() => themeIcons[theme.mode])
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

const menuEntries: Array<{ key: string; labelKey: string; icon: Component }> = [
  { key: '/', labelKey: 'app.menu.dashboard', icon: IconDashboard },
  { key: '/routes', labelKey: 'app.menu.routes', icon: IconRoute },
  { key: '/services', labelKey: 'app.menu.services', icon: IconService },
  { key: '/extensions', labelKey: 'app.menu.extensions', icon: IconExtension },
  { key: '/global-settings', labelKey: 'app.menu.globalSettings', icon: IconSettings },
  { key: '/connect', labelKey: 'app.menu.connect', icon: IconConnect },
]
const menuOptions = computed<MenuOption[]>(() =>
  menuEntries.map(({ key, labelKey, icon }) => ({
    label: t(labelKey),
    key,
    icon: () => h(NIcon, { size: 18 }, { default: () => h(icon) }),
  })),
)

const selectedMenu = computed(() => {
  if (route.path.startsWith('/services/')) return '/services'
  return route.path
})
const displayedConnectionLabel = computed(() =>
  connection.apiKey === null ? t('common.notConnected') : t('common.connected'),
)
const connectionTone = computed<'ok' | 'error' | 'idle'>(() => {
  if (connection.apiKey === null) return 'idle'
  return controllerError.value ? 'error' : 'ok'
})
const fullConnectionAddress = computed(() =>
  connection.apiKey === null ? t('common.notConnected') : (connection.baseUrl ?? window.location.origin))

function navigate(key: string): void {
  mobileDrawer.value = false
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

function openControllerConfig(): void {
  mobileDrawer.value = false
  void router.push('/controller-config')
}

async function retryControllerState(): Promise<void> {
  await controllerStateQuery.refetch()
}
</script>

<template>
  <n-config-provider
    :theme="appTheme"
    :theme-overrides="themeOverrides"
    :locale="naiveLocale"
    :date-locale="naiveDateLocale"
  >
    <n-message-provider>
      <n-dialog-provider>
        <n-layout v-if="isBare" class="bare-shell">
          <n-layout-content class="bare-content">
            <router-view />
          </n-layout-content>
        </n-layout>
        <n-layout v-else has-sider class="app-shell">
          <n-layout-sider
            v-if="!isMobile"
            class="app-sider"
            collapse-mode="width"
            :collapsed="siderCollapsed"
            :collapsed-width="64"
            :width="224"
            :native-scrollbar="false"
            bordered
          >
            <div class="sider-inner">
              <div class="brand" :class="{ 'brand--collapsed': siderCollapsed }">
                <div class="brand-mark"><n-icon :size="20"><IconCatHead /></n-icon></div>
                <div v-if="!siderCollapsed" class="brand-text">
                  <span class="brand-name">Nekostick</span>
                  <span class="brand-sub">Controller</span>
                </div>
              </div>
              <n-menu
                class="app-menu"
                :value="selectedMenu"
                :options="menuOptions"
                :collapsed="siderCollapsed"
                :collapsed-width="64"
                :collapsed-icon-size="20"
                @update:value="navigate"
              />
              <div class="sider-footer" :class="{ 'sider-footer--collapsed': siderCollapsed }">
                <button
                  type="button"
                  class="connection-chip"
                  :class="`connection-chip--${connectionTone}`"
                  :title="fullConnectionAddress"
                  @click="openControllerConfig"
                >
                  <span class="chip-icon"><span class="status-dot" :class="`status-dot--${connectionTone}`" /></span>
                  <span v-if="!siderCollapsed" class="connection-chip-label">{{ displayedConnectionLabel }}</span>
                </button>
                <div class="sider-controls">
                  <n-dropdown trigger="click" :options="themeOptions" @select="handleThemeSelect">
                    <n-button quaternary block>
                      <template #icon><n-icon :size="18"><component :is="themeIcon" /></n-icon></template>
                    </n-button>
                  </n-dropdown>
                  <n-dropdown trigger="click" :options="languageOptions" @select="handleLanguageSelect">
                    <n-button quaternary block>
                      <template #icon><n-icon :size="18"><IconGlobe /></n-icon></template>
                    </n-button>
                  </n-dropdown>
                  <n-button quaternary block @click="siderCollapsed = !siderCollapsed">
                    <template #icon>
                      <n-icon :size="18"><component :is="siderCollapsed ? IconExpand : IconCollapse" /></n-icon>
                    </template>
                  </n-button>
                </div>
              </div>
            </div>
          </n-layout-sider>
          <n-layout>
            <n-layout-header v-if="isMobile" class="mobile-header" bordered>
              <n-button quaternary circle @click="mobileDrawer = true">
                <template #icon><n-icon :size="20"><IconMenu /></n-icon></template>
              </n-button>
              <div class="brand brand--mini">
                <div class="brand-mark"><n-icon :size="16"><IconCatHead /></n-icon></div>
                <span class="brand-name">Nekostick</span>
              </div>
              <div class="mobile-header-spacer" />
              <button
                type="button"
                class="connection-chip"
                :class="`connection-chip--${connectionTone}`"
                @click="openControllerConfig"
              >
                <span class="chip-icon"><span class="status-dot" :class="`status-dot--${connectionTone}`" /></span>
                <span class="connection-chip-label">{{ displayedConnectionLabel }}</span>
              </button>
            </n-layout-header>
            <n-layout-content class="app-content">
              <n-space vertical size="small">
                <BootstrapBanner :state="controllerState" />
                <ConnectionLostBanner
                  :error="controllerError"
                  :fetching="controllerFetching"
                  @retry="retryControllerState"
                />
                <router-view v-slot="{ Component }">
                  <transition name="view-fade" mode="out-in">
                    <component :is="Component" :key="route.fullPath" />
                  </transition>
                </router-view>
              </n-space>
            </n-layout-content>
          </n-layout>
        </n-layout>
        <n-drawer v-model:show="mobileDrawer" placement="left" :width="264">
          <n-drawer-content body-content-style="padding: 0 0 12px; display: flex; flex-direction: column; gap: 12px" closable>
            <div class="brand brand--drawer">
              <div class="brand-mark"><n-icon :size="20"><IconCatHead /></n-icon></div>
              <div class="brand-text">
                <span class="brand-name">Nekostick</span>
                <span class="brand-sub">Controller</span>
              </div>
            </div>
            <n-menu :value="selectedMenu" :options="menuOptions" @update:value="navigate" />
            <div class="drawer-controls">
              <n-dropdown trigger="click" :options="themeOptions" @select="handleThemeSelect">
                <n-button quaternary class="drawer-control-button">
                  <template #icon><n-icon :size="18"><component :is="themeIcon" /></n-icon></template>
                  {{ t(themeModeLabelKeys[theme.mode]) }}
                </n-button>
              </n-dropdown>
              <n-dropdown trigger="click" :options="languageOptions" @select="handleLanguageSelect">
                <n-button quaternary class="drawer-control-button">
                  <template #icon><n-icon :size="18"><IconGlobe /></n-icon></template>
                  {{ languageLabels[i18nState.locale] }}
                </n-button>
              </n-dropdown>
            </div>
          </n-drawer-content>
        </n-drawer>
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

.bare-shell {
  height: 100vh;
}

.bare-content :deep(.n-layout-scroll-container) {
  height: 100vh;
  overflow: hidden;
  padding: 0;
}

.app-content :deep(.n-layout-scroll-container) {
  height: 100%;
  overflow-y: auto;
  padding: 24px;
}

.app-sider {
  background: var(--n-card-color);
}

.sider-inner {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
}

.brand {
  align-items: center;
  display: flex;
  gap: 10px;
  padding: 18px 16px 14px;
}

.brand--collapsed {
  justify-content: center;
  padding: 18px 0 14px;
}

.brand-mark {
  align-items: center;
  background: linear-gradient(135deg, #f2a7c8 0%, #c084fc 100%);
  border-radius: 10px;
  box-shadow: 0 2px 10px rgba(226, 132, 178, 0.35);
  color: #2b1220;
  display: flex;
  flex: 0 0 auto;
  height: 34px;
  justify-content: center;
  width: 34px;
}

.brand--mini {
  padding: 0;
}

.brand--mini .brand-mark {
  border-radius: 8px;
  height: 28px;
  width: 28px;
}

.brand--drawer {
  padding: 4px 4px 0;
}

.brand-text {
  display: flex;
  flex-direction: column;
  line-height: 1.2;
  min-width: 0;
}

.brand-name {
  font-weight: 700;
  letter-spacing: 0.01em;
  white-space: nowrap;
}

.brand-sub {
  color: var(--n-text-color-3);
  font-size: 11px;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  white-space: nowrap;
}

.app-menu {
  flex: 1 1 auto;
}

.app-menu:not(.n-menu--collapsed) {
  padding: 4px 8px;
}

.sider-footer {
  border-top: 1px solid var(--n-border-color);
  display: flex;
  flex-direction: column;
  flex: 0 0 auto;
  gap: 4px;
  padding: 8px;
}

.sider-footer--collapsed {
  align-items: center;
}

.sider-controls {
  display: flex;
  gap: 4px;
}

/* Dropdown triggers render their button as a direct child: three equal thirds,
   icon centered inside each. */
.sider-controls > * {
  flex: 1 1 0;
  min-width: 0;
}

.sider-footer--collapsed .sider-controls {
  align-items: center;
  flex-direction: column;
}

.sider-footer--collapsed .sider-controls > * {
  flex: 0 0 auto;
  width: auto;
}

/* Connection status pill: plain button so the tone tint, spacing and ellipsis are
   fully under our control (n-button's inner flex fights all three). Geometry mirrors
   the n-menu items above: same height, dot centered on the menu icon column (icon slot
   starts 33px from the sider content edge), label on the menu text column. */
.connection-chip {
  align-items: center;
  background: transparent;
  border: none;
  border-radius: 8px;
  color: inherit;
  cursor: pointer;
  display: flex;
  font: inherit;
  font-size: 14px;
  gap: 10px;
  height: 42px;
  max-width: 100%;
  padding: 0 14px 0 33px;
  transition: background-color 0.15s ease;
}

.chip-icon {
  display: flex;
  flex: 0 0 auto;
  justify-content: center;
  width: 18px;
}

.connection-chip--ok {
  background: color-mix(in srgb, var(--ns-ok) 10%, transparent);
  color: var(--ns-ok);
}

.connection-chip--ok:hover {
  background: color-mix(in srgb, var(--ns-ok) 18%, transparent);
}

.connection-chip--error {
  background: color-mix(in srgb, var(--ns-error) 10%, transparent);
  color: var(--ns-error);
}

.connection-chip--error:hover {
  background: color-mix(in srgb, var(--ns-error) 18%, transparent);
}

.connection-chip--idle:hover {
  background: color-mix(in srgb, currentColor 8%, transparent);
}

.connection-chip:focus-visible {
  outline: 2px solid currentColor;
  outline-offset: 1px;
}

.sider-footer .connection-chip {
  width: 100%;
}

.sider-footer--collapsed .connection-chip {
  justify-content: center;
  padding: 0;
  width: 42px;
}

.sider-footer--collapsed .chip-icon {
  width: auto;
}

.connection-chip-label {
  font-weight: 400;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mobile-header {
  align-items: center;
  display: flex;
  gap: 12px;
  padding: 8px 12px;
}

.mobile-header-spacer {
  flex: 1 1 auto;
}

.mobile-header .connection-chip-label {
  max-width: 140px;
}

.drawer-controls {
  border-top: 1px solid var(--n-border-color);
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-top: auto;
  padding: 8px 8px 0;
}

.drawer-control-button {
  justify-content: flex-start;
  width: 100%;
}

@media (max-width: 899px) {
  .app-content :deep(.n-layout-scroll-container) {
    padding: 16px;
  }
}
</style>
