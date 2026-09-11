export type JsonPrimitive = string | number | boolean | null;
export type JsonValue = JsonPrimitive | JsonObject | JsonValue[];
export type JsonObject = { [key: string]: JsonValue };

export interface Envelope<T> {
  apiVersion: 1;
  ok: boolean;
  code: string;
  message: string;
  data: T | null;
  version: number | null;
}
export type ControllerResponseEnvelope<T> = Envelope<T>;

export type RouteMatcherType =
  | 'Exact'
  | 'ExactCaseInsensitive'
  | 'Prefix'
  | 'PrefixCaseInsensitive'
  | 'Regex';
export type RouteTargetType = 'Microservice' | 'StaticFile' | 'ExtensionHandler';
export type ForwardingMode = 'Preserve' | 'Strip' | 'Replace';
export type HeaderRewriteOperation = 'Remove' | 'Set' | 'Add';
export type ServiceStartMode = 'Eager' | 'Lazy';
export type ServiceRestartPolicy = 'Never' | 'OnFailure' | 'Always';
export type HealthCheckType = 'Process' | 'Tcp' | 'Http';
export type RateLimitRejectionBehavior = 'Reject' | 'Queue';
export type RateLimitRetryAfterBehavior = 'None' | 'FromReplenishmentPeriod';
export type ExtensionLoadState = 'Discovered' | 'Loaded' | 'Stopped' | 'Failed' | 'Unloading' | 'Disabled';
export type ServiceLifecycleState =
  | 'Unknown'
  | 'Disabled'
  | 'Starting'
  | 'Running'
  | 'Stopping'
  | 'Failed'
  | 'waiting';
export type ServiceHealthState = 'Unknown' | 'Healthy' | 'Unhealthy';

// Backend-prefixed aliases make the wire contract names available to callers
// that share terminology with the controller implementation.
export type ControllerRouteMatcherType = RouteMatcherType;
export type ControllerRouteTargetKind = RouteTargetType;
export type ControllerForwardingMode = ForwardingMode;
export type ControllerHeaderRewriteOperation = HeaderRewriteOperation;
export type ControllerServiceStartMode = ServiceStartMode;
export type ControllerServiceRestartPolicy = ServiceRestartPolicy;
export type ControllerServiceHealthCheckType = HealthCheckType;
export type ControllerRateLimitRejectionBehavior = RateLimitRejectionBehavior;
export type ControllerRateLimitRetryAfterBehavior = RateLimitRetryAfterBehavior;
export type ControllerExtensionLoadState = ExtensionLoadState;
export type ControllerServiceLifecycleState = ServiceLifecycleState;
export type ControllerServiceHealthState = ServiceHealthState;

export interface RouteMatcher {
  type: RouteMatcherType;
  pattern: string;
  hostPatterns: string[];
  methods: string[];
}

export type RouteMatcherDto = RouteMatcher;

export interface RouteTarget {
  type: RouteTargetType;
  serviceId: string | null;
  rootPath: string | null;
  handlerId: string | null;
}

export type RouteTargetDto = RouteTarget;

export interface RouteTargetWrite {
  type: RouteTargetType;
  serviceId?: string | null;
  rootPath?: string | null;
  handlerId?: string | null;
}

export type RouteTargetWriteDto = RouteTargetWrite;

export interface HeaderRewrite {
  operation: HeaderRewriteOperation;
  name: string;
  value: string | null;
}

export type HeaderRewriteDto = HeaderRewrite;

export interface HeaderRewriteWrite {
  operation: HeaderRewriteOperation;
  name: string;
  value?: string | null;
}

export type HeaderRewriteWriteDto = HeaderRewriteWrite;

export interface Forwarding {
  mode: ForwardingMode;
  replaceTemplate: string | null;
}

export type ForwardingDto = Forwarding;

export interface ForwardingWrite {
  mode: ForwardingMode;
  replaceTemplate?: string | null;
}

export type ForwardingWriteDto = ForwardingWrite;

export interface ClientIpRatePolicy {
  tokenLimit: number;
  tokensPerPeriod: number;
  replenishmentPeriodMs: number;
  queueLimit: number;
  rejectionBehavior: RateLimitRejectionBehavior;
  retryAfterBehavior: RateLimitRetryAfterBehavior;
}

export type ClientIpRatePolicyDto = ClientIpRatePolicy;

export interface ProxyTimeouts {
  connectTimeoutMs: number;
  httpActivityTimeoutMs: number;
  httpTotalTimeoutMs: number;
  webSocketIdleTimeoutMs: number;
}

export type ProxyTimeoutDto = ProxyTimeouts;

export interface ProxyRetries {
  maxRetries: number;
  initialBackoffMs: number;
  maximumBackoffMs: number;
  retryOnConnectionFailure: boolean;
  retryOnUpstreamDisconnect: boolean;
}

export type ProxyRetryDto = ProxyRetries;

export interface GlobalSettings {
  version: number;
  autoPortRangeStart: number;
  autoPortRangeEnd: number;
  maxRequestBodyBytes: number;
  maxRequestHeaderBytes: number;
  maxConcurrentRequests: number;
  requestReadTimeoutMs: number;
  configurationPollIntervalMs: number;
  trustedProxyCidrs: string[];
  proxyTimeouts: ProxyTimeouts;
  clientIpRatePolicy: ClientIpRatePolicy | null;
  proxyRetries: ProxyRetries;
}

export type GlobalSettingsDto = GlobalSettings;

export interface RouteDto {
  readonly id: string;
  enabled: boolean;
  matcher: RouteMatcher;
  target: RouteTarget;
  priority: number;
  forwarding: Forwarding;
  requestHeaderRewrites: HeaderRewrite[];
  responseHeaderRewrites: HeaderRewrite[];
  metadataJson: string;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly version: number;
  clientIpRatePolicy: ClientIpRatePolicy | null;
  maxRequestBodyBytes: number | null;
  maxRequestHeaderBytes: number | null;
  maxConcurrentRequests: number | null;
  requestReadTimeoutMs: number | null;
  proxyRetries: ProxyRetries | null;
}

export interface RouteWriteDto {
  enabled: boolean;
  matcher: RouteMatcher | null;
  target: RouteTargetWrite | null;
  priority: number;
  forwarding: ForwardingWrite | null;
  requestHeaderRewrites: HeaderRewriteWrite[];
  responseHeaderRewrites: HeaderRewriteWrite[];
  metadataJson: string;
  clientIpRatePolicy: ClientIpRatePolicy | null;
  maxRequestBodyBytes: number | null;
  maxRequestHeaderBytes: number | null;
  maxConcurrentRequests: number | null;
  requestReadTimeoutMs: number | null;
  proxyRetries: ProxyRetries | null;
}
export type RouteCreateBody = RouteWriteDto;
export type RoutePatchBody = JsonObject | Partial<RouteWriteDto>;

export interface ProcessHealthCheck {
  type: 'Process';
  httpPath: string | null;
  timeoutMs: number;
}

export interface TcpHealthCheck {
  type: 'Tcp';
  httpPath: string | null;
  timeoutMs: number;
}

export interface HttpHealthCheck {
  type: 'Http';
  httpPath: string | null;
  timeoutMs: number;
}

export type HealthCheck = ProcessHealthCheck | TcpHealthCheck | HttpHealthCheck;
export type HealthCheckDto = HealthCheck;

export interface ProcessHealthCheckWrite {
  type: 'Process';
  httpPath?: null;
  timeoutMs: number;
}

export interface TcpHealthCheckWrite {
  type: 'Tcp';
  httpPath?: null;
  timeoutMs: number;
}

export interface HttpHealthCheckWrite {
  type: 'Http';
  httpPath: string;
  timeoutMs: number;
}

export type HealthCheckWrite = ProcessHealthCheckWrite | TcpHealthCheckWrite | HttpHealthCheckWrite;
export type HealthCheckWriteDto = HealthCheckWrite;

export interface ServiceDto {
  readonly id: string;
  enabled: boolean;
  fileName: string;
  argumentList: string[];
  workingDirectory: string;
  startMode: ServiceStartMode;
  restartPolicy: ServiceRestartPolicy;
  healthCheck: HealthCheck;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly version: number;
}

export interface ServiceWriteDto {
  enabled: boolean;
  fileName: string;
  argumentList: string[];
  workingDirectory: string;
  environment?: Record<string, string> | null;
  startMode: ServiceStartMode;
  restartPolicy: ServiceRestartPolicy;
  healthCheck?: HealthCheckWrite | null;
}
export type ServiceCreateBody = ServiceWriteDto;
export type ServicePatchBody = JsonObject | Partial<ServiceWriteDto>;

export interface ServiceEnvironment {
  serviceId: string;
  environment: Record<string, string>;
}

export type ServiceEnvironmentDto = ServiceEnvironment;
export interface ServiceEnvironmentWriteBody {
  environment: Record<string, string>;
}

export interface ServiceRuntimeSnapshot {
  serviceId: string;
  processId: number | null;
  startedAt: string | null;
  uptimeMs: number | null;
  lifecycleState: ServiceLifecycleState;
  healthState: ServiceHealthState;
  forwardedRequestCount: number;
  activeForwardedRequestCount: number;
  lastUpdatedAt: string | null;
  lastHealthAt: string | null;
  ownerExtensionId: string | null;
}

export type ServiceRuntimeDto = ServiceRuntimeSnapshot;

export type ServiceRuntimeActionOutcome = 'resumed' | 'ignored' | 'restarted';

export interface ServiceRuntimeActionResult {
  outcome: ServiceRuntimeActionOutcome;
}


export interface ListenerState {
  enabled: boolean;
  running: boolean;
}

export interface ControllerListenersState {
  hostRoute: ListenerState;
  httpJson: ListenerState;
  grpc: ListenerState;
  unixSocket: ListenerState;
}

export type ControllerHostSnapshotState = 'unknown' | 'accepted' | 'rejected';
export type ControllerHostReadiness = 'unknown' | 'unready' | 'ready' | 'degraded';

export interface ControllerHostInfo {
  nodeId: string | null;
  readOnly: boolean;
  extensionsSkipped: boolean;
  supervisorDisabled: boolean;
  databaseAvailable: boolean;
  snapshotAvailable: boolean;
  configurationValid: boolean;
  publishedConfigurationVersion: number | null;
  lastSnapshotState: ControllerHostSnapshotState;
  lastSnapshotStateAt: string | null;
  readiness: ControllerHostReadiness;
}

export interface ControllerWebUiState {
  embedded: boolean;
  enabled: boolean;
}

export interface ControllerState {
  bootstrapMode: boolean;
  listeners: ControllerListenersState;
  host: ControllerHostInfo | null;
  webUi: ControllerWebUiState;
}

export interface ExtensionRecord {
  extensionId: string;
  version: string;
  loadState: ExtensionLoadState;
  createdAt: string;
  updatedAt: string;
  recordVersion: number;
  isRunning: boolean;
  manifestVersion: string | null;
  contentHash: string | null;
}
export interface ExtensionInstallResult {
  id: string;
  version: string;
  replaced: boolean;
}

export interface ExtensionScanSkip {
  directoryName: string;
  failureCode: string;
}

export interface ExtensionRefreshSummary {
  added: string[];
  versionUpdated: string[];
  missing: string[];
  skipped: ExtensionScanSkip[] | null;
}

export interface ExtensionSettings {
  extensionId: string;
  schemaVersion: number;
  settings: JsonValue;
  version: number;
}

export interface ExtensionSettingsWriteBody {
  schemaVersion: number;
  settings: JsonValue;
}
export type ExtensionSettingsWriteDto = ExtensionSettingsWriteBody;
export type ServiceEnvironmentReadDto = ServiceEnvironment;
export type ServiceEnvironmentWriteDto = ServiceEnvironmentWriteBody;

export interface RootOverview {
  version: number;
  globalSettings: GlobalSettings;
  routes: RouteDto[];
  services: ServiceDto[];
  extensions: ExtensionRecord[];
}
