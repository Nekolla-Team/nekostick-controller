export const connect = {
  title: 'Connect to Nekostick Controller',
  baseUrl: 'Base URL',
  apiKey: 'API key',
  submit: 'Connect',
  failed: 'Connection failed: status={status} code={code} kind={kind}',
  failedUnknown: 'Connection failed: status=unknown code=unknown kind=network',
  storageNote: "The API key persists in this browser's localStorage.",
} as const
