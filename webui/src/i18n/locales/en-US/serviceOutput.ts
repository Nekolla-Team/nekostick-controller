export const serviceOutput = {
  title: 'Service Output',
  status: {
    connecting: 'Connecting',
    live: 'Streaming live',
    closed: 'Disconnected',
    error: 'Connection failed',
  },
  closed: {
    processExited: 'The process exited; the output stream ended.',
    sessionEnded: 'The management session ended (API key rotation or transport shutdown).',
    fault: 'The Host output stream faulted.',
    abnormal: 'The connection dropped (authentication may have failed, or this transport does not support output streaming).',
    unknown: 'The connection closed (code {code}).',
  },
  truncated: '...older output has been truncated...',
  empty: 'No output yet',
  clear: 'Clear',
  reconnect: 'Reconnect',
} as const
