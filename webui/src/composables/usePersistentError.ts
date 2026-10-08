import { computed, ref, watch, type ComputedRef } from 'vue'

/** Consecutive poll failures required before the query error is surfaced. */
const minConsecutiveFailures = 2

/** The slice of a query result this composable reads; vue-query exposes these as refs. */
interface QueryFailureSource {
  error: { readonly value: unknown }
  errorUpdatedAt: { readonly value: number }
  dataUpdatedAt: { readonly value: number }
}

/**
 * Surface a query error only once it repeated, latching it so the value never flickers across
 * poll cycles. vue-query exposes the raw per-poll failure, and that raw value is not stable:
 * a poll in flight clears `error` until the request settles (no cached data yet), and a settled
 * failure can be replaced by a differently classified one (e.g. an aborted `transport` after a
 * `network` drop), so a banner bound to it toggles every cycle even during a sustained outage.
 * Counting consecutive failures keeps it hidden for the first `minConsecutiveFailures` polls
 * and keeps the last failure latched afterwards; fresh data resets both, so a genuine outage
 * still surfaces quickly and clears on the first success.
 *
 * `isTransient` marks failures the caller can dismiss (e.g. a controller reload dropping an
 * in-flight request); they neither count nor latch, and are judged when first observed.
 */
export function usePersistentError(
  source: QueryFailureSource,
  isTransient: (error: unknown) => boolean = () => false,
): { error: ComputedRef<unknown> } {
  const consecutiveFailures = ref(0)
  const lastError = ref<unknown>(null)

  watch(
    () => source.errorUpdatedAt.value,
    () => {
      const error = source.error.value
      if (error === null || error === undefined || isTransient(error)) return
      consecutiveFailures.value += 1
      lastError.value = error
    },
    { immediate: true },
  )
  watch(
    () => source.dataUpdatedAt.value,
    () => {
      consecutiveFailures.value = 0
      lastError.value = null
    },
  )

  return {
    error: computed(() =>
      consecutiveFailures.value >= minConsecutiveFailures ? lastError.value : null,
    ),
  }
}
