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
} as const
