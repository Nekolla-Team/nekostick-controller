# Nekostick Controller 管理 API 参考

`nekolla.nekostick.controller` 通过本地传输提供同机管理 API。当前公共协议是 `apiVersion: 1`，需要 Host API `>=1.3.1 <2.0.0`。

所有启用的传输共享同一套资源、认证、JSON、并发和错误语义。控制器不绑定远程 TCP 地址。

## 1. 资源一览

路径以规范形式书写；root 路径的可选末尾 `/` 会在对应传输规则中说明：

| Method | Path | 用途 |
| --- | --- | --- |
| `GET` | `/v1` | 读取 API 根信息和配置概览 |
| `GET` | `/v1/controller/state` | 读取 bootstrap 模式、listener 运行状态和 Web UI 状态 |
| `GET` | `/`（HTTP/JSON、Unix socket）；`hostRoutePath/`（HostRoute） | 读取内嵌的 SPA 外壳（无需 API key） |
| `GET` | `/<file>`（HTTP/JSON、Unix socket）；`hostRoutePath/<file>`（HostRoute） | 读取内嵌的 Web UI 静态资源（无需 API key） |
| `GET`, `PATCH` | `/v1/global-settings` | 读取或更新全局设置 |
| `GET`, `POST` | `/v1/routes` | 列出或创建 route |
| `GET`, `PATCH`, `DELETE` | `/v1/routes/{id}` | 读取、更新或删除 route |
| `GET`, `POST` | `/v1/services` | 列出或创建 service |
| `GET`, `PATCH`, `DELETE` | `/v1/services/{id}` | 读取、更新或删除 service |
| `GET` | `/v1/services/runtime` | 读取所有服务的 runtime telemetry |
| `GET` | `/v1/services/{id}/runtime` | 读取单个服务的 runtime telemetry |
| `POST` | `/v1/services/{id}/runtime/resume` | 在本节点恢复处于 waiting 的 service（Host API >=1.3.3） |
| `POST` | `/v1/services/{id}/runtime/restart` | 在本节点严格重启 service（Host API >=1.3.3） |
| `GET`, `PUT`, `DELETE` | `/v1/services/{id}/environment` | 读取、替换或清空 service environment |
| `GET` | `/v1/extensions` | 列出 extension records |
| `GET` | `/v1/extensions/{id}` | 读取 extension record |
| `GET`, `PUT`, `DELETE` | `/v1/extensions/{id}/settings` | 读取、替换或删除 extension settings |
| `POST` | `/v1/extensions/{id}/enable`、`/v1/extensions/{id}/disable`、`/v1/extensions/{id}/reload` | 启用、禁用或重载一个 extension record |
| `DELETE` | `/v1/extensions/{id}/record` | 级联删除一个 extension record |
| `POST` | `/v1/extensions/refresh` | 重新扫描扩展目录并返回摘要（含 `skipped`，Host API >=1.3.4） |
| `POST` | `/v1/extensions/install` | 流式上传扩展 zip 包并安装/整体替换扩展目录（HostRoute 需 Host API >=1.3.2） |

route/service `{id}` 是 GUID；extension `{id}` 是不含 `/` 的非空字符串。已识别资源上的其他 method 返回 `405 method_not_allowed`，未知路径返回 `404 not_found`。

RouteEvents 的 observation、hook、stream、callback 和相关 API 不受支持。

## 2. 传输

### 2.1 HostRoute

HostRoute 由 Host 挂到配置的 `hostRoutePath`；Host 决定实际 HTTP 暴露方式。控制器不会创建公网入口。

实际路径规则：

- `hostRoutePath == /v1`：使用 `/v1/...`；
- 其他路径：使用 `<hostRoutePath>/v1/...`。

例如 `hostRoutePath` 为 `/admin` 时，global settings 的实际路径是 `/admin/v1/global-settings`。控制器会把挂载前缀还原为逻辑 `/v1` 路径后处理。

控制器自己的 HostRoute 是私有保留 route：公共列表会隐藏它，按 ID 读取返回 `404`，创建、修改或删除该保留边界返回 `409 reserved_route`。

### 2.2 HTTP/JSON

启用后只监听 loopback HTTP/1.1 明文：

```text
http://127.0.0.1:<httpPort>/v1/...
http://[::1]:<httpPort>/v1/...
```

API key 放在 header `x-nekostick-controller-key` 中。该传输不提供 HTTPS。

### 2.3 gRPC

gRPC 提供一个通用 unary gateway，不另定义资源 RPC：

```text
nekostick.controller.management.v1.ControllerManagement/Invoke
```

`InvokeRequest` 的字段：

| 字段 | 内容 |
| --- | --- |
| `method` | REST method，例如 `GET`、`PATCH` |
| `path` | 逻辑 `/v1/...` 路径 |
| `headers` | `content-type`、`if-match` 等普通 header |
| `body` | canonical JSON request 的 UTF-8 bytes；GET 使用空 bytes |

API key 只能放在 gRPC metadata 的 `x-nekostick-controller-key`。把认证 header 放进 protobuf envelope 会得到 `400 invalid_request`。可交付的 `InvokeResponse` 保留与 HTTP 相同的 status、headers 和 canonical JSON body；不要只读取粗粒度 `InvokeResponse.code`。

### 2.4 Unix-domain socket

Unix socket 承载 HTTP/1.1，仅在非 Windows 平台可用。客户端连接配置的绝对 socket path，并像 HTTP 请求一样发送逻辑路径和 API key header。

启动时最终 socket path 必须不存在；已有文件、目录、symlink 或 reparse point 不会被覆盖。父目录必须是安全的真实目录，group/other 不可写，也不能经过 `/tmp` 或 `/private/tmp`。socket 文件模式固定并验证为 `0600`。停止时只有在所有权和安全性仍可证明时才删除 socket。

### 2.5 Web UI

Web UI 在各传输的 controller root 提供内嵌的 SPA 外壳，并在同一 root 提供它的静态资源：

- HTTP/JSON 和 Unix socket：仅精确的 `GET /` 提供外壳页面；`GET /<file>` 提供同名内嵌资源。
- HostRoute：`GET <hostRoutePath>/` 提供外壳页面，`GET <hostRoutePath>/<file>` 提供资源；仅 Host API `>=1.3.2` 且成功注册 streaming handler 时启用。不带尾斜杠的 `GET <hostRoutePath>` 返回 `302` 重定向到 `<hostRoutePath>/`，否则相对资源路径会落到 root 之外。`<hostRoutePath>/v1/...` 等更深路径仍然进入管理 API。
- gRPC 不提供 Web UI 页面或资源。

`<file>` 只接受单层、含扩展名、由 ASCII 字母数字与 `.` `-` `_` 组成的名字（最长 128 字符）。命中内嵌资源时返回 `200`，`Content-Type` 按扩展名决定（`.js`、`.css`、`.json`、`.svg` 与 woff/woff2/ttf 字体），并带 `Cache-Control: public, max-age=31536000, immutable`（构建产物文件名含内容哈希）；未命中或形状不合法时 request 继续进入普通管理 API admission，因此 `GET /v1` 等路径不会被静态资源遮蔽。

页面响应的 `Content-Type` 为 `text/html`，不需要 `x-nekostick-controller-key`。仅接受 `GET`；其他 method 会继续进入普通管理 API admission。页面路径不添加 CORS response headers。

Web UI 由 controller extension settings 中的 `enableWebUi` 控制。该设置在缺省（无设置文档或字段缺失）时默认为 `true`，即安装内嵌 flavor 后无需任何配置即可使用；显式设置为 `false` 可关闭页面：

```json
{
  "enableWebUi": false
}
```

只有设置启用且程序集包含 Web UI 构建产物（`webui/dist`，以 `IncludeWebUi=true` 打包）时才会提供页面与静态资源；禁用、资源不存在或不满足 HostRoute streaming 条件时，request 会继续现有管线并返回普通的 `404`/认证响应。Host API `<1.3.2` 的 buffered HostRoute 不提供页面，root 请求保持普通 `404` 语义。

## 3. 认证与请求边界

所有启用传输都使用同一个 API key：

```http
x-nekostick-controller-key: <API_KEY>
```

key 必须恰好出现一次，长度 `32..4096`，不能包含空白。HTTP、Unix 和 HostRoute 使用 header；gRPC 使用 metadata。缺失、重复、弱值、超长或不匹配都会返回 `401 unauthorized`，不会透露具体失败原因。

主要请求限制：

| 项目 | 限制 |
| --- | --- |
| 请求 body | 最多 `1 MiB` |
| 响应 body | 最多 `1 MiB` |
| method | 非空，最多 32 字符 |
| path | 必须以 `/` 开头，最多 8192 字符；query/fragment 不允许 |
| header 名称 | 最多 64 个，每个名称最多 256 字符，不能重复 |
| header 值 | 每个名称最多 64 个值，每个值最多 16 KiB |
| aggregate headers | UTF-8 总数最多 64 KiB |
| gRPC 外层消息 | `1 MiB + 64 KiB + 32 KiB` |

带 body 的写入要求恰好一个 `Content-Type`。普通写入使用 `application/json`；PATCH 也可以使用 `application/merge-patch+json`。JSON 使用 camelCase、字符串 enum、最大深度 32，不允许未知字段、注释或尾逗号。`POST /v1/extensions/install` 是唯一例外：body 为 zip 字节流，上限 64 MiB，不经过上表的 1 MiB 限制。

## 4. 响应 envelope

成功响应：

```json
{
  "apiVersion": 1,
  "ok": true,
  "code": "ok",
  "message": "The operation completed.",
  "data": {},
  "version": 42
}
```

错误响应：

```json
{
  "apiVersion": 1,
  "ok": false,
  "code": "invalid_request",
  "message": "The management request is invalid.",
  "data": null,
  "version": null
}
```

| 字段 | 语义 |
| --- | --- |
| `apiVersion` | 固定为 `1` |
| `ok` | 是否成功 |
| `code` | 稳定机器码，客户端应按它分支 |
| `message` | 固定人类可读文本，不适合程序化分支 |
| `version` | 需要聚合版本的成功响应（包括 reload-settings）的 Host 聚合版本；state、runtime telemetry 和 runtime action 固定为 `null` |

持久化配置和 `reload-settings` 成功响应带强 ETag，例如 `ETag: "42"`；state、runtime telemetry 和 runtime action 成功响应不带 ETag。唯一带 ETag 的失败响应是 extension settings 的 `404 no_settings`（见 6.6），它同样携带聚合版本，供客户端直接完成条件创建。

### 状态码

| Status | envelope `code` | 含义 |
| 200 | `ok` | GET、PATCH、PUT、runtime GET、runtime action POST、state GET 或 settings reload 成功 |
| 201 | `ok` | POST 创建成功；带 `Location` |
| 204 | 无 body | DELETE 成功；响应 header 带新 ETag |
| 400 | `invalid_request` | JSON、header、body、ID、path 或资源语义无效 |
| 401 | `unauthorized` | API key 无效 |
| 404 | `transport_disabled` | 当前 transport 未启用 |
| 404 | `not_found` | 资源不存在 |
| 404 | `no_settings` | extension record 存在但没有 settings 文档；响应带聚合 ETag |
| 405 | `method_not_allowed` | 已识别资源不支持该 method |
| 409 | `reserved_route` | 操作触及控制器保留 route |
| 409 | `downgrade_forbidden` | 扩展安装包版本低于已安装版本 |
| 412 | `precondition_failed` | `If-Match` 已过期 |
| 428 | `precondition_required` | mutation 缺少 `If-Match` |
| 501 | `unsupported` | Host API、能力或操作不支持 |
| 503 | `unavailable` | controller 或 Host bridge 不可用 |
| 503 | `storage_unavailable` | Host 配置存储或 runtime 数据不可用 |
| 503 | `response_too_large` | 响应超过 `1 MiB` |

transport-level admission 失败时，HTTP/Unix 可能直接返回空 body 的 `400`，没有 canonical envelope。客户端不要强行解析这种 body。

## 5. ETag 与 Merge Patch

这些规则适用于持久化配置 mutation，以及需要以 Host 聚合版本为条件的 `POST /v1/controller/reload-settings`；state 和 runtime telemetry GET 不适用。

以下操作都要求恰好一个强 quoted aggregate `If-Match`：

- `POST /v1/controller/reload-settings`；
- `PATCH /v1/global-settings`；
- `POST`、`PATCH`、`DELETE` route 资源；
- `POST`、`PATCH`、`DELETE` service 资源；
- `PUT`、`DELETE` service environment；
- `PUT`、`DELETE` extension settings。

合法格式是 `If-Match: "<非负十进制版本>"`。不接受 `W/`、`*`、列表、未加引号数字、空值或前导零（`"0"` 除外）。

- 缺失：`428 precondition_required`；
- 格式或数量错误：`400 invalid_request`；
- 版本过期或 CAS 失败：`412 precondition_failed`；
- 服务端只尝试一次，不自动重试。

PATCH 使用 JSON Merge Patch：body 必须是 object；`null` 删除字段；object 递归合并；数组整体替换；未知字段无效。PATCH 不能写服务器字段，也不能修改 service `environment`；environment 必须使用专用 subresource。

## 6. 资源字段与示例

### 6.1 根资源

`GET /v1` 返回配置概览。示例 envelope 的 `data`：

```json
{
  "version": 42,
  "globalSettings": {
    "version": 4,
    "autoPortRangeStart": 20000,
    "autoPortRangeEnd": 29999,
    "maxRequestBodyBytes": 1048576,
    "maxRequestHeaderBytes": 65536,
    "maxConcurrentRequests": 128,
    "requestReadTimeoutMs": 30000,
    "configurationPollIntervalMs": 5000,
    "trustedProxyCidrs": [],
    "proxyTimeouts": {
      "connectTimeoutMs": 5000,
      "httpActivityTimeoutMs": 30000,
      "httpTotalTimeoutMs": 60000,
      "webSocketIdleTimeoutMs": 120000
    },
    "clientIpRatePolicy": null,
    "proxyRetries": {
      "maxRetries": 2,
      "initialBackoffMs": 100,
      "maximumBackoffMs": 1000,
      "retryOnConnectionFailure": true,
      "retryOnUpstreamDisconnect": false
    }
  },
  "routes": [],
  "services": [],
  "extensions": []
}
```

根 `version`、envelope `version` 和 ETag 是同一个聚合版本。私有 controller route、service environment 和 extension settings 不会嵌入这里。

### 6.2 Route

route 的主要可变字段是 `enabled`、`matcher`、`target`、`priority`、`forwarding`、request/response header rewrites、`metadataJson` 和可选 route 覆盖值。服务器字段 `id`、`createdAt`、`updatedAt`、`version` 只能读取。

创建示例：

```json
{
  "enabled": true,
  "matcher": {
    "type": "Prefix",
    "pattern": "/api",
    "hostPatterns": [],
    "methods": []
  },
  "target": {
    "type": "Microservice",
    "serviceId": "01234567-89ab-7cde-8f01-23456789abcd",
    "rootPath": null,
    "handlerId": null
  },
  "priority": 100,
  "forwarding": {
    "mode": "Preserve",
    "replaceTemplate": null
  },
  "requestHeaderRewrites": [],
  "responseHeaderRewrites": [],
  "metadataJson": "{\"owner\":\"example\"}",
  "clientIpRatePolicy": null,
  "maxRequestBodyBytes": null,
  "maxRequestHeaderBytes": null,
  "maxConcurrentRequests": null,
  "requestReadTimeoutMs": null,
  "proxyRetries": null
}
```

`matcher.type` 是 `Exact`、`ExactCaseInsensitive`、`Prefix`、`PrefixCaseInsensitive` 或 `Regex`。`target.type` 是 `Microservice`、`StaticFile` 或 `ExtensionHandler`。示例中的 `rootPath: null` 和 `handlerId: null` 只是明确表示未使用该 target 字段；普通 PATCH 也可以省略这些字段。`forwarding.mode` 是 `Preserve`、`Strip` 或 `Replace`。

### 6.3 Service

service 的可变字段是 `enabled`、`fileName`、`argumentList`、`workingDirectory`、`startMode`、`restartPolicy` 和 `healthCheck`。create 可一次性提供 `environment` map；service read DTO 永远不会内嵌 environment。

创建示例：

```json
{
  "enabled": true,
  "fileName": "/opt/example/app",
  "argumentList": ["--serve"],
  "workingDirectory": "/opt/example",
  "environment": {
    "EXAMPLE_TOKEN": "<secret>"
  },
  "startMode": "Lazy",
  "restartPolicy": "OnFailure",
  "healthCheck": {
    "type": "Http",
    "httpPath": "/healthz",
    "timeoutMs": 5000
  }
}
```

`startMode` 是 `Eager` 或 `Lazy`；`restartPolicy` 是 `Never`、`OnFailure` 或 `Always`；`healthCheck.type` 是 `Process`、`Tcp` 或 `Http`。

### 6.4 Service environment

这是唯一读取或修改 environment 的公共入口。GET 示例 envelope 的 `data`：

```json
{
  "serviceId": "01234567-89ab-7cde-8f01-23456789abcd",
  "environment": {
    "EXAMPLE_TOKEN": "<secret>"
  }
}
```

PUT body：

```json
{
  "environment": {
    "EXAMPLE_TOKEN": "<secret>"
  }
}
```

DELETE 清空 environment。environment value 可能包含 secret，应按敏感资料处理。

### 6.5 Service runtime telemetry

`GET /v1/services/runtime` 返回 snapshot 数组；`GET /v1/services/{id}/runtime` 返回单个 snapshot。两者都要求空 body，不接受 `If-Match`，成功 envelope 的 `version` 固定为 `null`，响应没有 ETag。

单个 snapshot 示例：

```json
{
  "serviceId": "01234567-89ab-7cde-8f01-23456789abcd",
  "processId": 1234,
  "startedAt": "2026-08-24T01:23:45.0000000+00:00",
  "uptimeMs": 123456,
  "lifecycleState": "Running",
  "healthState": "Healthy",
  "forwardedRequestCount": 1200,
  "activeForwardedRequestCount": 3,
  "lastUpdatedAt": "2026-08-24T01:25:00.0000000+00:00",
  "lastHealthAt": "2026-08-24T01:24:59.0000000+00:00"
}
```

字段语义：

| 字段 | 语义 |
| --- | --- |
| `serviceId` | service GUID |
| `processId` | 已知时为本机 process ID，否则 `null` |
| `startedAt` | 当前进程代次的 UTC 启动时间，未知为 `null` |
| `uptimeMs` | 当前进程代次的运行毫秒数，未知为 `null` |
| `lifecycleState` | `Unknown`、`Disabled`、`Starting`、`Running`、`Stopping`、`Failed`、`waiting` |
| `healthState` | `Unknown`、`Healthy`、`Unhealthy` |
| `forwardedRequestCount` | 累计转发请求数 |
| `activeForwardedRequestCount` | 当前转发中的请求数 |
| `lastUpdatedAt` | telemetry 最后更新时间，未知为 `null` |
| `lastHealthAt` | 最近健康检查时间，未知为 `null` |

控制器不会伪造 Host 没有的 telemetry。member 不存在时返回 `404 not_found`；能力不支持返回 `501 unsupported`；存储或 runtime 数据不可用返回 `503 storage_unavailable`。

#### 6.5.1 Service runtime actions

`POST /v1/services/{id}/runtime/resume` 用于在本节点主动恢复处于 `waiting` 的 service；`POST /v1/services/{id}/runtime/restart` 用于在本节点执行严格的停后启动。两者都要求请求 body 为空，不接受 `If-Match`，成功 envelope 的 `version` 固定为 `null`，响应不带 ETag。它们不会写入或同步全局配置。

resume 成功时 `data.outcome` 为 `resumed`；service 已经不在 waiting 状态而请求被忽略时为 `ignored`。restart 成功时始终为 `restarted`，不会返回 `ignored`。Host API 低于 `1.3.3` 时返回 `501 unsupported`；未知 service 返回 `404 not_found`；service 状态验证失败返回 `400 invalid_request`；其他 Host 错误按统一错误映射返回。

### 6.6 Extensions

extension record 是只读信息：

```json
{
  "extensionId": "nekolla.nekostick.controller",
  "version": "1.0.0",
  "loadState": "Loaded",
  "createdAt": "2026-08-24T00:00:00.0000000+00:00",
  "updatedAt": "2026-08-24T00:10:00.0000000+00:00",
  "recordVersion": 3,
  "contentHash": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
}
```

`loadState` 是 `Discovered`、`Loaded`、`Stopped`、`Failed` 或 `Unloading`。
`contentHash` 是最近一次 Host 扫描记录的扩展目录 SHA-256 摘要；尚未记录或 Host API 低于 `1.3.3` 时为 `null`。

`POST /v1/extensions/{id}/enable`、`POST /v1/extensions/{id}/disable`、`POST /v1/extensions/{id}/reload` 和 `DELETE /v1/extensions/{id}/record` 管理单个 extension record：启用、禁用、重载，以及级联删除记录（只删除记录，不删除磁盘上的扩展目录）。四者都要求请求 body 为空，不接受 `If-Match`，成功响应是无版本 envelope。未知 `{id}` 返回 `404 not_found`；reload 的目标 record 不处于 `Loaded` 时返回 `400 invalid_request`；Host 未提供管理能力时返回 `501 unsupported`。

reload 成功的 `data` 是 `{"outcome": "..."}`：

- 经 controller 自己的 listener（HTTP/JSON、Unix socket、gRPC）提交时为 `reloaded`，表示 Host 已完成本次 generation 替换；
- 经 HostRoute 提交时为 `scheduled`，表示 Host 只把这次 reload 排入 deferred publication。Host 在自己的 extension route callback 中禁止同步 reload，调度是该上下文唯一受支持的入口；调度是 best-effort，既不等待替换完成，也不回报后续失败，因此该上下文下无法调度时返回 `501 unsupported`。

enable、disable 和 record 三者在任何 transport 上的语义一致（Host 会把 route callback 中的 publish 推迟到 callback 返回之后）。

`POST /v1/extensions/refresh` 要求 Host 重新扫描扩展目录，请求 body 必须为空，成功响应是无版本 envelope（不带 ETag）：

```json
{
  "added": [],
  "versionUpdated": [],
  "missing": [],
  "skipped": [
    { "directoryName": "broken-ext", "failureCode": "ManifestMissing" }
  ]
}
```

`POST /v1/extensions/install` 上传扩展安装包。与其他管理端点不同，请求 body 是 zip 原始字节流（不是 JSON、不是 multipart），建议带 `Content-Type: application/zip`；安装包上限 64 MiB，解压总量上限 256 MiB，超限返回 `400 invalid_request`。zip 根目录必须有 `manifest.json`，至少包含合法的 `id` 与 semver `version` 字段。

安装目标目录是扩展根目录下的 `<id>/`，完全替换不做合并：目录已存在时先整体改名为 `<id>.bak` 备份，新目录就位后再删除备份；若备份就位后的替换步骤失败，会尝试从备份恢复原目录，此时 `503 storage_unavailable` 的 `message` 会明确说明恢复是否成功（未触及原目录的失败响应不包含恢复信息）。已安装版本高于上传版本时拒绝降级，返回 `409 downgrade_forbidden` 且不改动磁盘。版本相等允许替换；已有目录的 manifest 无法解析时视为可替换。安装成功后需要调用方自行触发 `POST /v1/extensions/refresh`（或等待 Host 自行扫描）让新扩展生效。成功响应是无版本 envelope：

```json
{
  "id": "nekolla.nekostick.example",
  "version": "1.4.2",
  "replaced": false
}
```

该端点只接受流式传输：HTTP/JSON 与 Unix socket 直接可用；HostRoute 需要 Host API >=1.3.2 的 streaming handler（旧 Host 的 buffered 路由返回 `501 unsupported`），且 body 上限受 Host 全局 `maxRequestBodyBytes` 约束。gRPC 不支持。其余错误：`400 invalid_request`（zip 损坏、缺少/非法 manifest、超限；`message` 会给出具体拒绝原因）、`409 downgrade_forbidden`（`message` 含已安装与上传版本号）、`503 storage_unavailable`（扩展目录不可写）。

`skipped` 逐项报告本次扫描中被跳过的目录：`directoryName` 是目录叶子名（不含完整路径），`failureCode` 是稳定的失败类别名（如 `ManifestMissing`、`JsonInvalid`）。Host API 低于 `1.3.4` 时 `skipped` 为 `null`；更早版本（低于 `1.3.1`）不提供该端点，返回 `501 unsupported`。

extension settings 是扩展自己的 opaque JSON。GET 返回：

```json
{
  "extensionId": "<extension-id>",
  "schemaVersion": 1,
  "settings": {
    "example": true
  },
  "version": 7
}
```

PUT body 只有 `schemaVersion` 和 `settings`；extension identity 来自 URL。extension record 必须已经存在。DELETE 只删除 settings，不删除 extension record。控制器不解释 `settings` 的业务字段。

record 存在但还没有持久化 settings 文档时（从未写过，或已被 DELETE 删除），`GET` 返回 `404 no_settings`（区别于 record 不存在时的 `404 not_found`），并且该失败响应仍带聚合 `ETag`——客户端可以直接把它作为 `If-Match` 提交 `PUT` 完成首次创建，不需要先读别的资源。客户端应把 `no_settings` 视为空文档。

## 7. Bootstrap 与安全

### 7.1 零 listener bootstrap

如果配置里 HostRoute、HTTP、gRPC、Unix 全部关闭，控制器会为当前启动实例临时启用 HostRoute：

1. 在内存中生成随机 route path 和高熵 API key；
2. 创建一个带私有 ownership marker 的临时 HostRoute；
3. 成功后通过 Host API 1.3 的 host-attributed LogWriter 一次性交付该 path 和 API key；
4. 停止或回滚时，只在 route identity、handler、path、matcher 和 marker 都能证明属于当前实例时删除它。

bootstrap credential 不写入持久化配置、响应或异常。正常配置的 API key 永远不会被记录。缺少 Host API 1.3 LogWriter 会使启动失败。

### 7.2 安全使用

- 不要把真实的 API key 或 environment value 写进日志、shell history、错误报告或示例；示例中的 `<API_KEY>` 和 `<secret>` 只是占位符。
- gRPC API key 只能放在 metadata；protobuf envelope 中的认证 header 会被拒绝。
- 写入前先读取最新 ETag；`412` 后由调用方重新读取并决定业务动作。
- Unix socket 使用可信本地父目录和 `0600`，不要放到公共临时目录。
- 私有 route 的 handler ID、marker 或随机路径不是认证材料；仍必须提供 API key。
- 客户端应按 status、envelope `code`、`ok` 和结构化 `data` 分支，不要依赖固定 `message` 文案。

## 8. 快速请求

下面示例假设启用了 loopback HTTP：

```bash
API_KEY='<配置中的 API key>'
BASE='http://127.0.0.1:48123'

curl --fail-with-body \
  --header "x-nekostick-controller-key: $API_KEY" \
  "$BASE/v1"
```

GET 请求的 body 必须为空。持久化配置响应会带强 ETag；成功 body 是统一 envelope。

### 条件更新示例

先读取资源并保存响应 header 中的 ETag，再把该版本作为 `If-Match` 提交：

```bash
curl --fail-with-body \
  --request PATCH \
  --header "x-nekostick-controller-key: $API_KEY" \
  --header "Content-Type: application/merge-patch+json" \
  --header 'If-Match: "42"' \
  --data '{"enabled": false}' \
  "$BASE/v1/services/01234567-89ab-7cde-8f01-23456789abcd"
```

`412 precondition_failed` 表示版本已过期。调用方应重新读取最新 ETag，再决定是否重试。

### 控制器状态与设置热重载

`GET /v1/controller/state` 返回当前 controller runtime state。它是只读、未版本化的状态读取：请求 body 必须为空，不接受 `If-Match`，成功响应不带 ETag 且 envelope `version` 固定为 `null`。响应包含 bootstrap 模式、listener 的 enablement/running 状态、始终存在的 `webUi` 对象，以及 Host API `>=1.3.3` 时可用的非敏感 `host` 快照；不包含设置值、端口、路径、API key 或其他 secret。`webUi.embedded` 表示程序集是否包含内嵌的 Web UI 构建产物，`webUi.enabled` 表示当前 `enableWebUi` 设置为 true 且资源存在。每次读取都会对照 Host 配置核实 HostRoute listener 状态；Host 配置暂时不可读时返回 `503 unavailable`。


```json
{
  "apiVersion": 1,
  "ok": true,
  "code": "ok",
  "message": "The operation completed.",
  "data": {
    "bootstrapMode": true,
    "listeners": {
      "hostRoute": { "enabled": true, "running": true },
      "httpJson": { "enabled": false, "running": false },
      "grpc": { "enabled": false, "running": false },
      "unixSocket": { "enabled": false, "running": false }
    },
    "webUi": {
      "embedded": true,
      "enabled": true
    },
    "host": {
      "nodeId": "node-a",
      "readOnly": false,
      "extensionsSkipped": false,
      "supervisorDisabled": false,
      "databaseAvailable": true,
      "snapshotAvailable": true,
      "configurationValid": true,
      "publishedConfigurationVersion": 42,
      "lastSnapshotState": "accepted",
      "lastSnapshotStateAt": "2026-08-24T01:25:00.0000000+00:00",
      "readiness": "ready"
    }
  },
  "version": null
}
```

`host` 只在 Host API `>=1.3.3` 且 `ExtensionHostInfoSnapshot` 可用时返回对象；Host API 低于 `1.3.3`，或 Host 尚未提供有效快照时为 `null`。字段均为非敏感状态：`nodeId` 可为 `null`，`publishedConfigurationVersion` 和 `lastSnapshotStateAt` 可为 `null`；`lastSnapshotState` 的值为 `unknown`、`accepted` 或 `rejected`，`readiness` 的值为 `unknown`、`unready`、`ready` 或 `degraded`。

| 字段 | 语义 |
| --- | --- |
| `nodeId` | Host 稳定节点 ID，未知时为 `null` |
| `readOnly` | Host 是否禁用配置写入 |
| `extensionsSkipped` | Host 是否禁用 extension 加载 |
| `supervisorDisabled` | Host 是否禁用 service supervision |
| `databaseAvailable` | 最近一次运行操作中数据库是否可用 |
| `snapshotAvailable` | 当前是否发布完整配置快照 |
| `configurationValid` | 当前发布配置是否有效 |
| `publishedConfigurationVersion` | 发布配置版本，未知时为 `null` |
| `lastSnapshotState` | 最近快照结果：`unknown`、`accepted` 或 `rejected` |
| `lastSnapshotStateAt` | 最近快照状态变更时间，未知时为 `null` |
| `readiness` | Host readiness：`unknown`、`unready`、`ready` 或 `degraded` |


`POST /v1/controller/reload-settings` 从 Host 重新读取 `nekolla.nekostick.controller` extension settings，并在不重新加载 extension 的情况下应用它们。请求 body 必须为空，并且必须恰好包含一个强聚合 `If-Match`；先读取任一持久化配置资源的 ETag，再提交重载：

```bash
curl --fail-with-body \
  --request POST \
  --header "x-nekostick-controller-key: $API_KEY" \
  --header 'If-Match: "42"' \
  --data-binary '' \
  "$BASE/v1/controller/reload-settings"
```

成功响应为 `200 ok`，`data` 使用与 state endpoint 相同的 `bootstrapMode` 和 `listeners` 形状，并同时包含 `webUi` 对象（该路径不同步读取 Host 快照，`host` 字段恒为 `null`，Host 信息仅在 `GET /v1/controller/state` 返回）；响应 ETag 和 envelope `version` 是本次 Host snapshot 读取使用的当前聚合版本。重载本身不会写回持久化配置。listener enablement、以及可安全协调的 HTTP/gRPC 端口和 HostRoute/Unix socket 路径变化会热应用，无需 extension reload。成功切换 API key 后新 key 立即生效，旧 key 立即失效；任何 key 都不会出现在响应或日志中。

重载是串行化的：执行期间其他配置写操作会排队等待，listener 切换期间新请求可能短暂收到 `503 unavailable`。如果重载停止或更换了承载该请求本身的 listener（例如关闭对应传输或修改端口），`200` 响应可能无法送达；此时通过其余 listener 的 state endpoint 确认重载结果。

设置无效或 listener 协调失败时，控制器会在安全可行时恢复之前的工作配置；无法安全恢复时会停止全部 listener 并拒绝后续请求，fail closed。bootstrap 模式只有在所有明确配置的 listener 都成功启动且 bootstrap route 已安全清理后才会切换为 configured mode。所有 listener 都关闭的设置在 bootstrap 模式下会被拒绝（`400 invalid_request`，bootstrap 保持运行）；在 configured mode 下会被应用：全部 listener 停止后管理 API 完全不可达，恢复需要通过 Host 修改 settings 并重新加载 extension。

重载的 `If-Match` 错误遵循聚合 CAS 规则：缺少 header 返回 `428 precondition_required`，数量、body 或强 ETag 格式不正确返回 `400 invalid_request`，版本过期返回 `412 precondition_failed`。客户端应重新读取最新 ETag 后再决定是否重试。
