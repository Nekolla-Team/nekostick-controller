export const globalSettings = {
  title: 'Global settings',
  subtitle: 'Modify the controller listener, limits, and proxy policies.',
  save: 'Save settings',
  saveSuccess: 'Global settings saved',
  errors: {
    settingsNotLoaded: 'Settings have not loaded',
  },
  validation: {
    portRangeOrder: 'Auto port range start must not be greater than end',
    portRangeBounds: 'Port range must be between 1 and 65535',
  },
  cards: {
    portAndRequestLimits: 'Port and request limits',
  },
  sections: {
    proxyTimeouts: 'Proxy timeouts',
    proxyRetries: 'Proxy retries',
    clientIpRatePolicy: 'Client IP rate policy',
  },
  fields: {
    autoPortRangeStart: 'Auto port range start',
    autoPortRangeEnd: 'Auto port range end',
    maxRequestBodyBytes: 'Max request body bytes',
    maxRequestHeaderBytes: 'Max request header bytes',
    maxConcurrentRequests: 'Max concurrent requests',
    requestReadTimeout: 'Request read timeout (ms)',
    configurationPollInterval: 'Configuration poll interval (ms)',
    trustedProxyCidrs: 'Trusted proxy CIDRs',
    proxyTimeouts: {
      connectTimeout: 'Connect timeout (ms)',
      httpActivityTimeout: 'HTTP activity timeout (ms)',
      httpTotalTimeout: 'HTTP total timeout (ms)',
      webSocketIdleTimeout: 'WebSocket idle timeout (ms)',
    },
    proxyRetries: {
      maxRetries: 'Max retries',
      initialBackoff: 'Initial backoff (ms)',
      maximumBackoff: 'Maximum backoff (ms)',
      retryOnConnectionFailure: 'Retry on connection failure',
      retryOnUpstreamDisconnect: 'Retry on upstream disconnect',
    },
    clientIpRatePolicy: {
      status: 'Configuration status',
      configured: 'Configured',
      notConfigured: 'Not configured',
      tokenLimit: 'Token limit',
      tokensPerPeriod: 'Tokens per period',
      replenishmentPeriod: 'Replenishment period (ms)',
      queueLimit: 'Queue limit',
      rejectionBehavior: {
        label: 'Rejection behavior',
        reject: 'Reject',
        queue: 'Queue',
      },
      retryAfterBehavior: {
        label: 'Retry-After behavior',
        none: 'None',
        fromReplenishmentPeriod: 'From replenishment period',
      },
    },
  },
} as const
