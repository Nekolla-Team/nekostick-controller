export const serviceRuntime = {
  title: 'Service runtime status',
  snapshotTitle: 'Runtime snapshot',
  status: {
    lifecycle: 'Lifecycle: {state}',
    waiting: 'Waiting',
    health: 'Health: {state}',
  },
  actions: {
    restart: 'Restart',
    resume: 'Resume',
  },
  confirm: {
    restart: 'Restart this service?',
  },
  feedback: {
    restartSuccess: 'Service restarted',
    resumeSuccess: 'Service resumed',
    resumeIgnored: 'Resume ignored because the service was not waiting',
  },
  uptime: {
    days: '{days}d',
    hours: '{hours}h',
    minutes: '{minutes}m',
    seconds: '{seconds}s',
  },
  details: {
    uptime: 'Uptime',
    processId: 'PID',
    forwardedRequests: 'Forwarded requests',
    activeForwarded: 'Active forwarded requests',
    startedAt: 'Started at',
    lastUpdatedAt: 'Last updated',
    lastHealthAt: 'Last health check',
    owner: 'Owner extension',
  },
} as const
