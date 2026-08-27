<script setup lang="ts">
import { computed } from 'vue'
import { NAlert, NButton, NSpace } from 'naive-ui'
import type { ControllerState } from '../api/types'
import { useRouter } from 'vue-router'
import { t } from '../i18n'

const props = defineProps<{ state: ControllerState | undefined }>()
const emit = defineEmits<{ 'edit-config': [] }>()
const router = useRouter()
const visible = computed(() => props.state?.bootstrapMode === true)

function open(path: string): void {
  void router.push(path)
}
</script>

<template>
  <n-alert
    v-if="visible"
    type="warning"
    :show-icon="true"
    :closable="false"
    :title="t('app.bootstrapBanner.title')"
  >
    {{ t('app.bootstrapBanner.body') }}
    <n-space>
      <n-button size="small" type="warning" @click="emit('edit-config')">
        {{ t('app.bootstrapBanner.editControllerConfig') }}
      </n-button>
      <n-button size="small" @click="open('/connect')">
        {{ t('app.bootstrapBanner.openConnect') }}
      </n-button>
    </n-space>
  </n-alert>
</template>
