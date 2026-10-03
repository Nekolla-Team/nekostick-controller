import type { ServiceHealthState, ServiceLifecycleState } from './api/types'
import { t } from './i18n'

export type StatusTagType = 'default' | 'success' | 'warning' | 'error'

export function lifecycleTagType(state: ServiceLifecycleState): StatusTagType {
  if (state === 'Running') return 'success'
  if (state === 'Starting' || state === 'Stopping' || state === 'waiting') return 'warning'
  if (state === 'Failed') return 'error'
  return 'default'
}

export function lifecycleLabel(state: ServiceLifecycleState): string {
  if (state === 'waiting') return t('serviceRuntime.status.waiting')
  if (state === 'stopped') return t('serviceRuntime.status.stopped')
  return state
}

export function healthTagType(state: ServiceHealthState): StatusTagType {
  if (state === 'Healthy') return 'success'
  if (state === 'Unhealthy') return 'error'
  return 'default'
}
