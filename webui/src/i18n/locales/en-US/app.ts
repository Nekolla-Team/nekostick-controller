export const app = {
  title: 'Nekostick Controller',
  menu: {
    dashboard: 'Dashboard',
    routes: 'Routes',
    services: 'Services',
    extensions: 'Extensions',
    globalSettings: 'Global Settings',
    connect: 'Connection',
  },
  connection: {
    modalTitle: 'Connection',
    address: 'Address: {label}',
    hint: 'The API key is only stored in the local connection settings and is never shown here.',
    open: 'Open connection settings',
  },
  theme: {
    label: 'Theme',
    light: 'Light',
    dark: 'Dark',
    system: 'System',
  },
  language: {
    label: 'Language',
    zhCN: '简体中文',
    enUS: 'English',
  },
  bootstrapBanner: {
    title: 'Controller is in bootstrap mode',
    body: 'The controller is running on a temporary HostRoute with a one-time random key. Configure a permanent key in the extension settings first, then enable and configure a production listener in Global Settings.',
    openGlobalSettings: 'Open global settings',
    openConnect: 'Connection',
  },
  connectionLost: {
    title: 'Connection lost',
    body: 'The controller is temporarily unavailable. The saved connection key is kept. Retry after the listener recovers, or open Connection to change the address.',
    retry: 'Retry',
    openConnect: 'Connection',
  },
} as const
