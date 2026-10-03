export const serviceRuntime = {
  title: '服务运行状态',
  snapshotTitle: '运行时快照',
  unavailable: '当前没有可获取的运行时状态，服务可能未在运行',
  status: {
    lifecycle: '生命周期：{state}',
    waiting: '等待中',
    stopped: '已停止',
    health: '健康：{state}',
  },
  failure: {
    title: '故障详情',
    reason: '故障原因：{reason}',
    code: '故障代码：{code}',
    stage: '故障阶段：{stage}',
  },
  actions: {
    restart: '重启',
    resume: '恢复运行',
    output: '输出',
  },
  confirm: {
    restart: '确定重启此服务？',
  },
  feedback: {
    restartSuccess: '服务已重启',
    resumeSuccess: '服务已恢复运行',
    resumeIgnored: '服务当前不在等待状态，已忽略恢复操作',
  },
  uptime: {
    days: '{days} 天',
    hours: '{hours} 小时',
    minutes: '{minutes} 分',
    seconds: '{seconds} 秒',
  },
  details: {
    uptime: '运行时长',
    processId: '进程 ID',
    forwardedRequests: '已转发请求数',
    activeForwarded: '活跃转发请求',
    startedAt: '启动时间',
    lastUpdatedAt: '最后更新时间',
    lastHealthAt: '最近健康检查时间',
    retryAt: '下次重试时间',
    owner: '属主扩展',
  },
} as const
