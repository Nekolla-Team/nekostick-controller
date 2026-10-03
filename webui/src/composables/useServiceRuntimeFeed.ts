import { onBeforeUnmount, ref, watch } from 'vue'
import { useQueryClient } from '@tanstack/vue-query'
import {
  openServiceRuntimeFeed,
  type ServiceRuntimeFeedHandle,
} from '../api/serviceRuntimeFeed'
import type { ServiceRuntimeFeedEntry, ServiceRuntimeSnapshot } from '../api/types'
import { connection } from '../stores/connection'

const runtimeQueryKey = ['services', 'runtime'] as const
const initialReplayIdleMs = 500
const reconnectBaseDelayMs = 1000
const reconnectMaxDelayMs = 30000

export function useServiceRuntimeFeed() {
  const queryClient = useQueryClient()
  const pollingEnabled = ref(true)
  let handle: ServiceRuntimeFeedHandle | null = null
  let replayIdleTimer: ReturnType<typeof setTimeout> | null = null
  let retryTimer: ReturnType<typeof setTimeout> | null = null
  let subscriptionVersion = 0
  let reconnectAttempts = 0
  let disposed = false

  function resetRuntimeSnapshots(): void {
    void queryClient.cancelQueries({ queryKey: runtimeQueryKey, exact: true })
    queryClient.setQueryData<ServiceRuntimeSnapshot[]>(runtimeQueryKey, [])
  }

  function clearReplayIdleTimer(): void {
    if (replayIdleTimer !== null) {
      clearTimeout(replayIdleTimer)
      replayIdleTimer = null
    }
  }

  function clearRetryTimer(): void {
    if (retryTimer !== null) {
      clearTimeout(retryTimer)
      retryTimer = null
    }
  }

  function stopCurrentFeed(): void {
    subscriptionVersion += 1
    handle?.close()
    handle = null
    clearReplayIdleTimer()
    clearRetryTimer()
  }

  function finishInitialReplay(): void {
    clearReplayIdleTimer()
    pollingEnabled.value = false
    reconnectAttempts = 0
  }

  function applyEntry(entry: ServiceRuntimeFeedEntry): void {
    if (entry.kind === 'removed') {
      queryClient.setQueryData<ServiceRuntimeSnapshot[]>(runtimeQueryKey, current =>
        (current ?? []).filter(snapshot => snapshot.serviceId !== entry.serviceId))
      queryClient.removeQueries({
        queryKey: ['services', entry.serviceId, 'runtime'],
        exact: true,
      })
      return
    }

    const snapshot = entry.snapshot
    if (!snapshot) return
    void queryClient.cancelQueries({
      queryKey: ['services', entry.serviceId, 'runtime'],
      exact: true,
    })
    queryClient.setQueryData<ServiceRuntimeSnapshot[]>(runtimeQueryKey, current => {
      const existing = current ?? []
      const index = existing.findIndex(item => item.serviceId === entry.serviceId)
      if (index === -1) return [...existing, snapshot]
      const next = [...existing]
      next[index] = snapshot
      return next
    })
    queryClient.setQueryData(['services', entry.serviceId, 'runtime'], snapshot)
  }

  function scheduleReconnect(version: number): void {
    if (disposed || version !== subscriptionVersion || retryTimer !== null) return
    pollingEnabled.value = true
    const delay = Math.min(reconnectBaseDelayMs * 2 ** reconnectAttempts, reconnectMaxDelayMs)
    reconnectAttempts = Math.min(reconnectAttempts + 1, 5)
    retryTimer = setTimeout(() => {
      retryTimer = null
      if (disposed || version !== subscriptionVersion) return
      connect()
    }, delay)
  }

  function connect(): void {
    if (disposed) return
    clearRetryTimer()
    const apiKey = connection.apiKey
    if (!apiKey) {
      pollingEnabled.value = true
      return
    }

    pollingEnabled.value = true
    const version = ++subscriptionVersion
    let receivedEntry = false
    let resetForSubscription = false

    const resetBeforeApplyingEntry = (): void => {
      if (resetForSubscription) return
      resetRuntimeSnapshots()
      resetForSubscription = true
    }
    const scheduleReplayIdleCheck = (): void => {
      clearReplayIdleTimer()
      replayIdleTimer = setTimeout(() => {
        replayIdleTimer = null
        if (disposed || version !== subscriptionVersion) return
        if (!receivedEntry) resetRuntimeSnapshots()
        finishInitialReplay()
      }, initialReplayIdleMs)
    }

    handle = openServiceRuntimeFeed(apiKey, {
      onOpen: () => {
        if (disposed || version !== subscriptionVersion) return
        scheduleReplayIdleCheck()
      },
      onEntry: (entry) => {
        if (disposed || version !== subscriptionVersion) return
        receivedEntry = true
        resetBeforeApplyingEntry()
        applyEntry(entry)
        if (entry.isInitialSnapshot) {
          scheduleReplayIdleCheck()
        } else {
          finishInitialReplay()
        }
      },
      onEnd: () => {
        if (disposed || version !== subscriptionVersion) return
        handle = null
        clearReplayIdleTimer()
        scheduleReconnect(version)
      },
      onFailure: (failure) => {
        if (disposed || version !== subscriptionVersion) return
        handle = null
        clearReplayIdleTimer()
        if (failure.kind === 'http' && failure.status === 501) {
          pollingEnabled.value = true
          return
        }
        scheduleReconnect(version)
      },
    })
  }

  const stopWatchingConnection = watch(
    () => [connection.baseUrl, connection.apiKey] as const,
    () => {
      stopCurrentFeed()
      reconnectAttempts = 0
      pollingEnabled.value = true
      if (connection.apiKey) connect()
    },
    { immediate: true },
  )

  onBeforeUnmount(() => {
    disposed = true
    stopWatchingConnection()
    stopCurrentFeed()
  })

  return { pollingEnabled }
}
