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
} as const
