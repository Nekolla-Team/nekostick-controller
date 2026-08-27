import { computed, reactive, ref } from 'vue'

const STORAGE_KEY = 'nekostick.webui.theme'

export type ThemeMode = 'light' | 'dark' | 'system'

export const theme = reactive<{ mode: ThemeMode }>({ mode: 'system' })

const systemDark = ref(false)
let mediaQuery: MediaQueryList | null = null

function getStorage(): Storage | null {
  if (typeof window === 'undefined') return null

  try {
    return window.localStorage
  } catch {
    return null
  }
}

function isThemeMode(value: unknown): value is ThemeMode {
  return value === 'light' || value === 'dark' || value === 'system'
}

function watchSystemTheme(): void {
  if (typeof window === 'undefined' || mediaQuery !== null) return

  mediaQuery = window.matchMedia('(prefers-color-scheme: dark)')
  systemDark.value = mediaQuery.matches
  mediaQuery.addEventListener('change', (event) => {
    systemDark.value = event.matches
  })
}

export function loadTheme(): void {
  watchSystemTheme()

  try {
    const raw = getStorage()?.getItem(STORAGE_KEY)
    if (!raw) return

    const parsed: unknown = JSON.parse(raw)
    if (typeof parsed === 'object' && parsed !== null) {
      const mode = (parsed as Record<string, unknown>).mode
      if (isThemeMode(mode)) {
        theme.mode = mode
      }
    }
  } catch {
    theme.mode = 'system'
  }
}

export function setThemeMode(mode: ThemeMode): void {
  theme.mode = mode

  try {
    getStorage()?.setItem(STORAGE_KEY, JSON.stringify({ mode }))
  } catch {
    // Keep the in-memory selection when browser storage is unavailable.
  }
}

export const isDarkTheme = computed(() =>
  theme.mode === 'dark' || (theme.mode === 'system' && systemDark.value),
)
