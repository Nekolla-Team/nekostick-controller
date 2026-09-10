export const dashboard = {
  title: 'Dashboard',
  subtitle: 'View controller listener status and configuration overview.',
  reload: {
    button: 'Reload settings',
    success: 'Settings reloaded',
    uncertain: 'The reload request result is unknown. Verify whether the listener has switched through the controller state.',
    completed: {
      bootstrap: 'Reload completed: the controller remains in bootstrap mode.',
      configured: 'Reload completed: the controller entered configured mode.',
    },
  },
  listener: {
    title: 'Listener status',
    labels: {
      hostRoute: 'HostRoute',
      httpJson: 'HTTP / JSON',
      grpc: 'gRPC',
      unixSocket: 'Unix socket',
    },
    state: {
      running: 'Running',
      enabledNotRunning: 'Enabled but not running',
    },
  },
  mode: {
    title: 'Runtime mode',
    bootstrap: 'Bootstrap mode',
    configured: 'Configured mode',
  },
  overview: {
    title: 'Configuration overview',
    version: 'Version',
    routes: 'Routes',
    services: 'Services',
    extensions: 'Extensions',
    notAvailable: '—',
  },
  host: {
    title: 'Host information',
    nodeId: 'Node ID',
    readOnly: 'Read-only',
    extensionsSkipped: 'Extensions skipped',
    supervisorDisabled: 'Supervisor disabled',
    databaseAvailable: 'Database available',
    snapshotAvailable: 'Snapshot available',
    configurationValid: 'Configuration valid',
    publishedConfigurationVersion: 'Published configuration version',
    lastSnapshotState: 'Last snapshot state',
    lastSnapshotStateAt: 'Last snapshot state at',
    readiness: 'Readiness',
    snapshotStates: {
      unknown: 'Unknown',
      accepted: 'Accepted',
      rejected: 'Rejected',
    },
    readinessStates: {
      unknown: 'Unknown',
      unready: 'Unready',
      ready: 'Ready',
      degraded: 'Degraded',
    },
  },
  webUi: {
    title: 'Web UI',
    embedded: 'Embedded',
    enabled: 'Enabled',
  },
} as const
