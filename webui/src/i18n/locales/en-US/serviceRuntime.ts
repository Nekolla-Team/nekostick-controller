export const serviceRuntime = {
  title: 'Service runtime status',
  snapshotTitle: 'Runtime snapshot',
  unavailable: 'No runtime status is currently available; the service may not be running',
  status: {
    lifecycle: 'Lifecycle: {state}',
    waiting: 'Waiting',
    stopped: 'Stopped',
    health: 'Health: {state}',
  },
  failure: {
    title: 'Failure details',
    reason: 'Failure reason: {reason}',
    code: 'Failure code: {code}',
    stage: 'Failure stage: {stage}',
  },
  actions: {
    restart: 'Restart',
    resume: 'Resume',
    output: 'Output',
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
    retryAt: 'Next retry at',
    owner: 'Owner extension',
  },
} as const
