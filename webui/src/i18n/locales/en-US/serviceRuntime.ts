export const serviceRuntime = {
  title: 'Service runtime status',
  snapshotTitle: 'Runtime snapshot',
  status: {
    lifecycle: 'Lifecycle: {state}',
    health: 'Health: {state}',
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
