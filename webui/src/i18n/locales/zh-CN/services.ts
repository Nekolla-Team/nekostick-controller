export const services = {
  title: '服务',
  subtitle: '管理服务进程、健康检查和环境变量。',
  create: '新建服务',
  validation: {
    fileNameRequired: 'fileName 不能为空',
  },
  errors: {
    saveEnvironmentFallback: '保存环境变量失败',
    clearEnvironmentFallback: '清空环境变量失败',
    noServiceSelected: '未选择服务',
  },
  columns: {
    file: '文件',
    startMode: '启动模式',
    restartPolicy: '重启策略',
    healthCheck: '健康检查',
  },
  actions: {
    environmentVariables: '环境变量',
    runtimeStatus: '运行状态',
  },
  confirm: {
    deleteService: '确定删除此服务？',
    clearEnvironment: '确定清空该服务的全部环境变量？',
  },
  modal: {
    edit: '编辑服务',
    create: '新建服务',
  },
  form: {
    fileName: 'fileName',
    argumentList: 'argumentList',
    workingDirectory: 'workingDirectory',
    startMode: '启动模式',
    restartPolicy: '重启策略',
    healthCheck: '健康检查类型',
    httpPath: 'HTTP 路径',
    timeout: '超时（毫秒）',
    healthTypeOptions: {
      process: 'Process',
      tcp: 'TCP',
      http: 'HTTP',
    },
  },
  enums: {
    startModes: {
      eager: 'Eager',
      lazy: 'Lazy',
    },
    restartPolicies: {
      never: 'Never',
      onFailure: 'OnFailure',
      always: 'Always',
    },
    healthTypes: {
      process: 'Process',
      tcp: 'Tcp',
      http: 'Http',
    },
  },
  environment: {
    title: '环境变量',
    keyPlaceholder: 'KEY',
    valuePlaceholder: '值',
    remove: '移除',
    add: '添加变量',
    clear: '清空环境变量',
  },
} as const
