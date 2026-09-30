export const serviceOutput = {
  title: '服务输出',
  status: {
    connecting: '连接中',
    live: '实时推送中',
    closed: '已断开',
    error: '连接失败',
  },
  closed: {
    processExited: '进程已退出，输出流结束',
    sessionEnded: '管理会话已结束（API key 轮换或传输正在停止）',
    fault: 'Host 输出流故障',
    abnormal: '连接异常断开（可能是认证失败，或当前传输不支持输出流）',
    unknown: '连接已关闭（代码 {code}）',
  },
  truncated: '……较早的输出已被截断……',
  empty: '暂无输出',
  clear: '清空',
  reconnect: '重新连接',
} as const
