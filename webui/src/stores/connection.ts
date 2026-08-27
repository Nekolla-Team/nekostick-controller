import { computed, reactive } from 'vue'
import { t } from '../i18n'

const STORAGE_KEY = 'nekostick.webui.connection'

type StoredConnection = {
  baseUrl: string | null
  apiKey: string | null
}

type ConnectionState = {
  baseUrl: string | null
  apiKey: string | null
}

export const connection = reactive<ConnectionState>({
  baseUrl: null,
  apiKey: null,
})

function getStorage(): Storage | null {
  if (typeof window === 'undefined') return null

  try {
    return window.localStorage
  } catch {
    return null
  }
}

function isStoredConnection(value: unknown): value is StoredConnection {
  if (typeof value !== 'object' || value === null) return false

  const record = value as Record<string, unknown>
  return (
    (record.baseUrl === null || typeof record.baseUrl === 'string') &&
    (record.apiKey === null || typeof record.apiKey === 'string')
  )
}

export function loadConnection(): boolean {
  try {
    const storage = getStorage()
    const raw = storage?.getItem(STORAGE_KEY)
    if (!raw) {
      connection.baseUrl = null
      connection.apiKey = null
      return false
    }

    const parsed: unknown = JSON.parse(raw)
    if (!isStoredConnection(parsed)) {
      connection.baseUrl = null
      connection.apiKey = null
      return false
    }

    connection.baseUrl = parsed.baseUrl
    connection.apiKey = parsed.apiKey
    return true
  } catch {
    connection.baseUrl = null
    connection.apiKey = null
    return false
  }
}

export function saveConnection(baseUrl: string | null, apiKey: string | null): void {
  connection.baseUrl = baseUrl
  connection.apiKey = apiKey

  try {
    const storage = getStorage()
    if (!storage) return

    storage.setItem(STORAGE_KEY, JSON.stringify({ baseUrl, apiKey }))
  } catch {
    // Keep the in-memory connection when browser storage is unavailable.
  }
}

/** Updates the in-memory connection without persisting, for pre-save connectivity checks. */
export function stageConnection(baseUrl: string | null, apiKey: string | null): void {
  connection.baseUrl = baseUrl
  connection.apiKey = apiKey
}

export function clearConnection(): void {
  connection.baseUrl = null
  connection.apiKey = null

  try {
    getStorage()?.removeItem(STORAGE_KEY)
  } catch {
    // Clearing in-memory state is sufficient when browser storage is unavailable.
  }
}

export const connectionLabel = computed(() => {
  const { apiKey, baseUrl } = connection
  if (!apiKey) return t('common.notConnected')
  if (!baseUrl) return t('common.connected')
  let origin = baseUrl
  try {
    origin = new URL(baseUrl).origin
  } catch {
    // Keep the raw value when the configured address is not a valid URL.
  }
  return t('common.connectedTo', { baseUrl: origin })
})
