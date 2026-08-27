export const extensions = {
  title: '扩展',
  subtitle: '扩展记录只读；可单独编辑扩展 settings JSON。',
  columns: {
    extensionId: '扩展 ID',
    version: '版本',
    loadState: '加载状态',
    settings: '设置',
    editSettings: '编辑设置',
  },
  settings: {
    title: '扩展设置',
    deleteButton: '删除 settings',
    deleteConfirm: '确定删除该扩展的 settings？',
  },
  validation: {
    objectRequired: 'settings 必须是 JSON object',
    maxDepth: 'settings 的嵌套深度不能超过 32 层',
    invalidJson: 'settings 不是有效的 JSON object',
    invalid: 'settings 无效',
  },
} as const
