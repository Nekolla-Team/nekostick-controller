export const serviceRuntime = {
  title: '服务运行状态',
  snapshotTitle: '运行时快照',
  status: {
    lifecycle: '生命周期：{state}',
    health: '健康：{state}',
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
    owner: '属主扩展',
  },
} as const
