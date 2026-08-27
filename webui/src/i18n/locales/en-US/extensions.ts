export const extensions = {
  title: 'Extensions',
  subtitle: 'Extension records are read-only; extension settings JSON can be edited separately.',
  columns: {
    extensionId: 'Extension ID',
    version: 'Version',
    loadState: 'Load state',
    settings: 'Settings',
    editSettings: 'Edit settings',
  },
  settings: {
    title: 'Extension settings',
    deleteButton: 'Delete settings',
    deleteConfirm: "Delete this extension's settings?",
  },
  validation: {
    objectRequired: 'settings must be a JSON object',
    maxDepth: 'settings nesting depth cannot exceed 32 levels',
    invalidJson: 'settings is not a valid JSON object',
    invalid: 'settings is invalid',
  },
} as const
