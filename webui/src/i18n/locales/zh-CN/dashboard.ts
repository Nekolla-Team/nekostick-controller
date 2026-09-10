export const dashboard = {
  title: '仪表盘',
  subtitle: '查看控制器 listener 状态和配置概览。',
  reload: {
    button: '重载设置',
    success: '设置已重载',
    uncertain: '重载请求结果未知，请通过控制器状态确认 listener 是否已切换。',
    completed: {
      bootstrap: '重载完成：控制器仍处于引导模式。',
      configured: '重载完成：控制器已进入正式模式。',
    },
  },
  listener: {
    title: 'Listener 状态',
    labels: {
      hostRoute: 'HostRoute',
      httpJson: 'HTTP / JSON',
      grpc: 'gRPC',
      unixSocket: 'Unix socket',
    },
    state: {
      running: '运行中',
      enabledNotRunning: '已启用但未运行',
    },
  },
  mode: {
    title: '运行模式',
    bootstrap: '引导模式',
    configured: '正式模式',
  },
  overview: {
    title: '配置概览',
    version: '版本',
    routes: '路由',
    services: '服务',
    extensions: '扩展',
    notAvailable: '—',
  },
  host: {
    title: '宿主信息',
    nodeId: '节点 ID',
    readOnly: '只读模式',
    extensionsSkipped: '已跳过扩展',
    supervisorDisabled: 'Supervisor 已禁用',
    databaseAvailable: '数据库可用',
    snapshotAvailable: '快照可用',
    configurationValid: '配置有效',
    publishedConfigurationVersion: '已发布配置版本',
    lastSnapshotState: '最近快照状态',
    lastSnapshotStateAt: '最近快照状态时间',
    readiness: '就绪状态',
    snapshotStates: {
      unknown: '未知',
      accepted: '已接受',
      rejected: '已拒绝',
    },
    readinessStates: {
      unknown: '未知',
      unready: '未就绪',
      ready: '就绪',
      degraded: '降级',
    },
  },
  webUi: {
    title: 'Web UI',
    embedded: '已嵌入',
    enabled: '已启用',
  },
} as const
