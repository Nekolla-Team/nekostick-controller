<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { isDarkTheme } from '../stores/theme'

type MonacoApi = typeof import('../monaco')['default']
type Editor = ReturnType<typeof import('../monaco').default.editor.create>

const props = withDefaults(defineProps<{
  value: string
  height?: string
  label: string
}>(), { height: '240px' })

const emit = defineEmits<{ 'update:value': [string] }>()

const host = ref<HTMLDivElement | null>(null)
const api = shallowRef<MonacoApi | null>(null)
const instance = shallowRef<Editor | null>(null)
let contentSubscription: { dispose(): void } | null = null
let unmounted = false

onMounted(async () => {
  const container = host.value
  if (container === null) return

  // Monaco is a large dependency, so it is only fetched when an editor actually appears.
  const monaco = (await import('../monaco')).default
  if (unmounted) return

  api.value = monaco
  monaco.editor.setTheme(isDarkTheme.value ? 'vs-dark' : 'vs')
  const editor = monaco.editor.create(container, {
    value: props.value,
    language: 'json',
    automaticLayout: true,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
    wordWrap: 'on',
    tabSize: 2,
    fontSize: 13,
    formatOnPaste: true,
    fixedOverflowWidgets: true,
    ariaLabel: props.label,
  })
  contentSubscription = editor.onDidChangeModelContent(() => emit('update:value', editor.getValue()))
  instance.value = editor
})

watch(() => props.value, (value) => {
  const editor = instance.value
  if (editor === null || editor.getValue() === value) return
  editor.setValue(value)
})

watch(isDarkTheme, (dark) => api.value?.editor.setTheme(dark ? 'vs-dark' : 'vs'))

onBeforeUnmount(() => {
  unmounted = true
  contentSubscription?.dispose()
  instance.value?.getModel()?.dispose()
  instance.value?.dispose()
})
</script>

<template>
  <div ref="host" class="json-editor" :style="{ height }"></div>
</template>

<style scoped>
.json-editor {
  border: 1px solid var(--n-border-color, #e0e0e6);
  border-radius: 3px;
  overflow: hidden;
  /* Form items lay their content out as flex rows, so the host needs an explicit width. */
  width: 100%;
}
</style>
