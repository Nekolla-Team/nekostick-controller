import { reactive } from 'vue'
import { zhCN } from './locales/zh-CN'
import { enUS } from './locales/en-US'

const STORAGE_KEY = 'nekostick.webui.locale'

export type Locale = 'zh-CN' | 'en-US'

export const i18nState = reactive<{ locale: Locale }>({ locale: 'zh-CN' })

function getStorage(): Storage | null {
  if (typeof window === 'undefined') return null

  try {
    return window.localStorage
  } catch {
    return null
  }
}

export function loadLocale(): void {
  try {
    const stored = getStorage()?.getItem(STORAGE_KEY)
    if (stored === 'zh-CN' || stored === 'en-US') {
      i18nState.locale = stored
      return
    }
  } catch {
    // Fall through to browser detection.
  }

  i18nState.locale =
    typeof navigator !== 'undefined' && navigator.language.toLowerCase().startsWith('zh')
      ? 'zh-CN'
      : 'en-US'
}

export function setLocale(locale: Locale): void {
  i18nState.locale = locale

  try {
    getStorage()?.setItem(STORAGE_KEY, locale)
  } catch {
    // Keep the in-memory selection when browser storage is unavailable.
  }
}

function lookup(tree: unknown, path: string): string | null {
  let node: unknown = tree
  for (const segment of path.split('.')) {
    if (typeof node !== 'object' || node === null || !(segment in node)) return null
    node = (node as Record<string, unknown>)[segment]
  }
  return typeof node === 'string' ? node : null
}

export function t(key: string, params?: Record<string, string | number>): string {
  const messages = i18nState.locale === 'en-US' ? enUS : zhCN
  const template = lookup(messages, key) ?? lookup(zhCN, key) ?? key
  if (!params) return template

  return template.replace(/\{(\w+)\}/g, (match, name: string) =>
    name in params ? String(params[name]) : match)
}
