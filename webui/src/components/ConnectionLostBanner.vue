<script setup lang="ts">
import { computed } from 'vue'
import { NAlert, NButton, NSpace } from 'naive-ui'
import { ApiClientError } from '../api/client'
import { useRouter } from 'vue-router'
import { t } from '../i18n'

const props = defineProps<{
  error: unknown
  fetching: boolean
}>()
const emit = defineEmits<{ retry: [] }>()
const router = useRouter()
const visible = computed(() => {
  const error = props.error
  const kind = error instanceof ApiClientError ? String(error.kind) : ''
  return error !== null && error !== undefined && (
    kind === 'network' || kind === 'unavailable' || kind === 'transport_disabled'
  )
})

function connect(): void {
  void router.push('/connect')
}
</script>

<template>
  <n-alert
    v-if="visible"
    type="error"
    :show-icon="true"
    :closable="false"
    :title="t('app.connectionLost.title')"
  >
    {{ t('app.connectionLost.body') }}
    <n-space>
      <n-button size="small" type="error" :loading="fetching" @click="emit('retry')">
        {{ t('app.connectionLost.retry') }}
      </n-button>
      <n-button size="small" @click="connect">{{ t('app.connectionLost.openConnect') }}</n-button>
    </n-space>
  </n-alert>
</template>
