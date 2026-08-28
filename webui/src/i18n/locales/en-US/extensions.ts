export const extensions = {
  title: 'Extensions',
  subtitle: 'Inspect extension records and runtime state; enable, disable, reload, or manage records.',
  columns: {
    extensionId: 'Extension ID',
    version: 'Version',
    loadState: 'Load state',
    running: 'Running',
    manifestDrift: 'On-disk {version}',
    actions: 'Actions',
    settings: 'Settings',
    editSettings: 'Edit settings',
    enable: 'Enable',
    disable: 'Disable',
    reload: 'Reload',
    deleteRecord: 'Delete record',
    deleteRecordConfirm: 'Cascade-delete this extension record, settings, routes, and services? Only works once files are removed.',
  },
  refresh: {
    button: 'Refresh directory',
    summary: '{added} added, {updated} version-updated, {missing} missing',
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
