export const app = {
  title: 'Nekostick Controller',
  menu: {
    dashboard: '仪表盘',
    routes: '路由',
    services: '服务',
    extensions: '扩展',
    globalSettings: '全局设置',
    connect: '连接设置',
  },
  connection: {
    modalTitle: '连接设置',
    address: '连接地址: {label}',
    hint: 'API key 仅保存在本地连接设置中, 不会显示在此处。',
    open: '打开连接设置',
    editControllerConfig: '修改控制器配置',
  },
  theme: {
    label: '主题',
    light: '亮色',
    dark: '暗色',
    system: '系统',
  },
  language: {
    label: '语言',
    zhCN: '简体中文',
    enUS: 'English',
  },
  bootstrapBanner: {
    title: '控制器处于引导模式',
    body: '控制器正在临时 HostRoute 上运行, 并使用一次性随机 key。请先在扩展设置中配置永久 key, 再在全局设置中启用并配置正式 listener。',
    editControllerConfig: '更改控制器配置',
    openConnect: '连接设置',
  },
  connectionLost: {
    title: '连接丢失',
    body: '控制器暂时不可用, 已保存的连接 key 不会被清除。恢复 listener 后可重试, 或打开连接设置更换地址。',
    retry: '重试',
    openConnect: '连接设置',
  },
} as const
