export const services = {
  title: 'Services',
  subtitle: 'Manage service processes, health checks, and environment variables.',
  create: 'Create service',
  validation: {
    fileNameRequired: 'fileName cannot be empty',
  },
  errors: {
    saveEnvironmentFallback: 'Failed to save environment variables',
    clearEnvironmentFallback: 'Failed to clear environment variables',
    noServiceSelected: 'No service selected',
  },
  columns: {
    file: 'File',
    startMode: 'Start mode',
    restartPolicy: 'Restart policy',
    healthCheck: 'Health check',
  },
  actions: {
    environmentVariables: 'Environment variables',
    runtimeStatus: 'Runtime status',
  },
  confirm: {
    deleteService: 'Delete this service?',
    clearEnvironment: 'Clear all environment variables for this service?',
  },
  modal: {
    edit: 'Edit service',
    create: 'Create service',
  },
  form: {
    fileName: 'fileName',
    argumentList: 'argumentList',
    workingDirectory: 'workingDirectory',
    startMode: 'Start mode',
    restartPolicy: 'Restart policy',
    healthCheck: 'Health check type',
    httpPath: 'HTTP path',
    timeout: 'Timeout (ms)',
    healthTypeOptions: {
      process: 'Process',
      tcp: 'TCP',
      http: 'HTTP',
    },
  },
  enums: {
    startModes: {
      eager: 'Eager',
      lazy: 'Lazy',
    },
    restartPolicies: {
      never: 'Never',
      onFailure: 'OnFailure',
      always: 'Always',
    },
    healthTypes: {
      process: 'Process',
      tcp: 'Tcp',
      http: 'Http',
    },
  },
  environment: {
    title: 'Environment variables',
    keyPlaceholder: 'KEY',
    valuePlaceholder: 'Value',
    remove: 'Remove',
    add: 'Add variable',
    clear: 'Clear environment variables',
  },
} as const
