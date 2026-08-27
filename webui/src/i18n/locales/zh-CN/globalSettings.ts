export const globalSettings = {
  title: '全局设置',
  subtitle: '修改 controller 的 listener、限制和代理策略。',
  save: '保存设置',
  saveSuccess: '全局设置已保存',
  errors: {
    settingsNotLoaded: '设置尚未加载',
  },
  validation: {
    portRangeOrder: '自动端口范围起始值不能大于结束值',
    portRangeBounds: '端口范围必须在 1-65535 之间',
  },
  cards: {
    portAndRequestLimits: '端口与请求限制',
  },
  sections: {
    proxyTimeouts: '代理超时',
    proxyRetries: '代理重试',
    clientIpRatePolicy: '客户端 IP 速率限制策略',
  },
  fields: {
    autoPortRangeStart: '自动端口范围起始值',
    autoPortRangeEnd: '自动端口范围结束值',
    maxRequestBodyBytes: '最大请求正文大小（字节）',
    maxRequestHeaderBytes: '最大请求头大小（字节）',
    maxConcurrentRequests: '最大并发请求数',
    requestReadTimeout: '请求读取超时（毫秒）',
    configurationPollInterval: '配置轮询间隔（毫秒）',
    trustedProxyCidrs: '受信任的代理 CIDR',
    proxyTimeouts: {
      connectTimeout: '连接超时（毫秒）',
      httpActivityTimeout: 'HTTP 活动超时（毫秒）',
      httpTotalTimeout: 'HTTP 总超时（毫秒）',
      webSocketIdleTimeout: 'WebSocket 空闲超时（毫秒）',
    },
    proxyRetries: {
      maxRetries: '最大重试次数',
      initialBackoff: '初始退避（毫秒）',
      maximumBackoff: '最大退避（毫秒）',
      retryOnConnectionFailure: '连接失败时重试',
      retryOnUpstreamDisconnect: '上游断开时重试',
    },
    clientIpRatePolicy: {
      status: '配置状态',
      configured: '已配置',
      notConfigured: '未配置',
      tokenLimit: 'Token 上限',
      tokensPerPeriod: '每周期 Token 数',
      replenishmentPeriod: '补充周期（毫秒）',
      queueLimit: '队列上限',
      rejectionBehavior: {
        label: '拒绝行为',
        reject: 'Reject',
        queue: 'Queue',
      },
      retryAfterBehavior: {
        label: 'Retry-After 行为',
        none: 'None',
        fromReplenishmentPeriod: '按补充周期计算',
      },
    },
  },
} as const
