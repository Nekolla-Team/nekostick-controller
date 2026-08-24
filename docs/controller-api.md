# Nekostick Controller 管理 API 参考

`nekolla.nekostick.controller` 通过本地传输提供同机管理 API。当前公共协议是 `apiVersion: 1`，需要 Host API `>=1.3.0 <2.0.0`。

所有启用的传输共享同一套资源、认证、JSON、并发和错误语义。控制器不绑定远程 TCP 地址。

## 1. 快速请求

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

## 2. 资源一览

路径以规范形式书写，不带末尾 `/`：

| Method | Path | 用途 |
| --- | --- | --- |
| `GET` | `/v1` | 读取 API 根信息和配置概览 |
| `GET`, `PATCH` | `/v1/global-settings` | 读取或更新全局设置 |
| `GET`, `POST` | `/v1/routes` | 列出或创建 route |
| `GET`, `PATCH`, `DELETE` | `/v1/routes/{id}` | 读取、更新或删除 route |
| `GET`, `POST` | `/v1/services` | 列出或创建 service |
| `GET`, `PATCH`, `DELETE` | `/v1/services/{id}` | 读取、更新或删除 service |
| `GET` | `/v1/services/runtime` | 读取所有服务的 runtime telemetry |
| `GET` | `/v1/services/{id}/runtime` | 读取单个服务的 runtime telemetry |
| `GET`, `PUT`, `DELETE` | `/v1/services/{id}/environment` | 读取、替换或清空 service environment |
| `GET` | `/v1/extensions` | 列出 extension records |
| `GET` | `/v1/extensions/{id}` | 读取 extension record |
| `GET`, `PUT`, `DELETE` | `/v1/extensions/{id}/settings` | 读取、替换或删除 extension settings |

route/service `{id}` 是 GUID；extension `{id}` 是不含 `/` 的非空字符串。已识别资源上的其他 method 返回 `405 method_not_allowed`，未知路径返回 `404 not_found`。

RouteEvents 的 observation、hook、stream、callback 和相关 API 不受支持。

## 3. 传输

### 3.1 HostRoute

HostRoute 由 Host 挂到配置的 `hostRoutePath`；Host 决定实际 HTTP 暴露方式。控制器不会创建公网入口。

实际路径规则：

- `hostRoutePath == /v1`：使用 `/v1/...`；
- 其他路径：使用 `<hostRoutePath>/v1/...`。

例如 `hostRoutePath` 为 `/admin` 时，global settings 的实际路径是 `/admin/v1/global-settings`。控制器会把挂载前缀还原为逻辑 `/v1` 路径后处理。

控制器自己的 HostRoute 是私有保留 route：公共列表会隐藏它，按 ID 读取返回 `404`，创建、修改或删除该保留边界返回 `409 reserved_route`。

### 3.2 HTTP/JSON

启用后只监听 loopback HTTP/1.1 明文：

```text
http://127.0.0.1:<httpPort>/v1/...
http://[::1]:<httpPort>/v1/...
```

API key 放在 header `x-nekostick-controller-key` 中。该传输不提供 HTTPS。

### 3.3 gRPC

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

### 3.4 Unix-domain socket

Unix socket 承载 HTTP/1.1，仅在非 Windows 平台可用。客户端连接配置的绝对 socket path，并像 HTTP 请求一样发送逻辑路径和 API key header。

启动时最终 socket path 必须不存在；已有文件、目录、symlink 或 reparse point 不会被覆盖。父目录必须是安全的真实目录，group/other 不可写，也不能经过 `/tmp` 或 `/private/tmp`。socket 文件模式固定并验证为 `0600`。停止时只有在所有权和安全性仍可证明时才删除 socket。

## 4. 认证与请求边界

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

带 body 的写入要求恰好一个 `Content-Type`。普通写入使用 `application/json`；PATCH 也可以使用 `application/merge-patch+json`。JSON 使用 camelCase、字符串 enum、最大深度 32，不允许未知字段、注释或尾逗号。

## 5. 响应 envelope

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
| `data` | 资源结果；错误或 `204` 时为空 |
| `version` | 持久化配置成功响应的聚合版本；runtime telemetry 固定为 `null` |

持久化配置成功响应带强 ETag，例如 `ETag: "42"`。runtime telemetry 成功响应不带 ETag。

### 状态码

| Status | envelope `code` | 含义 |
| ---: | --- | --- |
| 200 | `ok` | GET、PATCH、PUT 或 runtime GET 成功 |
| 201 | `ok` | POST 创建成功；带 `Location` |
| 204 | 无 body | DELETE 成功；响应 header 带新 ETag |
| 400 | `invalid_request` | JSON、header、body、ID、path 或资源语义无效 |
| 401 | `unauthorized` | API key 无效 |
| 404 | `transport_disabled` | 当前 transport 未启用 |
| 404 | `not_found` | 资源不存在 |
| 405 | `method_not_allowed` | 已识别资源不支持该 method |
| 409 | `reserved_route` | 操作触及控制器保留 route |
| 412 | `precondition_failed` | `If-Match` 已过期 |
| 428 | `precondition_required` | mutation 缺少 `If-Match` |
| 501 | `unsupported` | Host API、能力或操作不支持 |
| 503 | `unavailable` | controller 或 Host bridge 不可用 |
| 503 | `storage_unavailable` | Host 配置存储或 runtime 数据不可用 |
| 503 | `response_too_large` | 响应超过 `1 MiB` |

transport-level admission 失败时，HTTP/Unix 可能直接返回空 body 的 `400`，没有 canonical envelope。客户端不要强行解析这种 body。

## 6. ETag 与 Merge Patch

这些规则只适用于持久化配置 mutation；两个 runtime telemetry GET 不适用。

以下操作都要求恰好一个强 quoted aggregate `If-Match`：

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

## 7. 资源字段与示例

### 7.1 根资源

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

### 7.2 Route

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

### 7.3 Service

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

### 7.4 Service environment

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

### 7.5 Service runtime telemetry

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
| `lifecycleState` | `Unknown`、`Disabled`、`Starting`、`Running`、`Stopping`、`Failed` |
| `healthState` | `Unknown`、`Healthy`、`Unhealthy` |
| `forwardedRequestCount` | 累计转发请求数 |
| `activeForwardedRequestCount` | 当前转发中的请求数 |
| `lastUpdatedAt` | telemetry 最后更新时间，未知为 `null` |
| `lastHealthAt` | 最近健康检查时间，未知为 `null` |

控制器不会伪造 Host 没有的 telemetry。member 不存在时返回 `404 not_found`；能力不支持返回 `501 unsupported`；存储或 runtime 数据不可用返回 `503 storage_unavailable`。

### 7.6 Extensions

extension record 是只读信息：

```json
{
  "extensionId": "nekolla.nekostick.controller",
  "version": "1.0.0",
  "loadState": "Loaded",
  "createdAt": "2026-08-24T00:00:00.0000000+00:00",
  "updatedAt": "2026-08-24T00:10:00.0000000+00:00",
  "recordVersion": 3
}
```

`loadState` 是 `Discovered`、`Loaded`、`Stopped`、`Failed` 或 `Unloading`。

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

## 8. Bootstrap 与安全

### 8.1 零 listener bootstrap

如果配置里 HostRoute、HTTP、gRPC、Unix 全部关闭，控制器会为当前启动实例临时启用 HostRoute：

1. 在内存中生成随机 route path 和高熵 API key；
2. 创建一个带私有 ownership marker 的临时 HostRoute；
3. 成功后通过 Host API 1.3 的 host-attributed LogWriter 一次性交付该 path 和 API key；
4. 停止或回滚时，只在 route identity、handler、path、matcher 和 marker 都能证明属于当前实例时删除它。

bootstrap credential 不写入持久化配置、响应或异常。正常配置的 API key 永远不会被记录。缺少 Host API 1.3 LogWriter 会使启动失败。

### 8.2 安全使用

- 不要把真实的 API key 或 environment value 写进日志、shell history、错误报告或示例；示例中的 `<API_KEY>` 和 `<secret>` 只是占位符。
- gRPC API key 只能放在 metadata；protobuf envelope 中的认证 header 会被拒绝。
- 写入前先读取最新 ETag；`412` 后由调用方重新读取并决定业务动作。
- Unix socket 使用可信本地父目录和 `0600`，不要放到公共临时目录。
- 私有 route 的 handler ID、marker 或随机路径不是认证材料；仍必须提供 API key。
- 客户端应按 status、envelope `code`、`ok` 和结构化 `data` 分支，不要依赖固定 `message` 文案。
