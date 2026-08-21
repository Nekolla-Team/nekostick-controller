# Nekostick Controller Host API 1.2 参考

## 1. 范围与公共边界

本文是 `nekolla.nekostick.controller` 的 Host API 1.2 管理面参考。公共协议版本为 `apiVersion: 1`，要求 Host 提供兼容的 `1.2` 配置能力。HostRoute handler ID 固定为 `nekolla.nekostick.controller.management`。

所有传输都把请求转换为同一个 transport-neutral request，再交给同一个 dispatcher；因此资源、认证、JSON、并发和错误语义不因传输而改变。控制器只接受本地边界内的请求，不提供远程 TCP 绑定。

### 1.1 唯一公共路径

以下是完整的公共资源面；路径按规范书写时不带末尾 `/`：

```text
GET                         /v1
GET, PATCH                  /v1/global-settings
GET, POST                   /v1/routes
GET, PATCH, DELETE          /v1/routes/{id}
GET, POST                   /v1/services
GET, PATCH, DELETE          /v1/services/{id}
GET, PUT, DELETE            /v1/services/{id}/environment
GET                         /v1/extensions
GET                         /v1/extensions/{id}
GET, PUT, DELETE            /v1/extensions/{id}/settings
```

`{id}` 是 route 或 service 的 GUID；extension ID 是不含 `/` 的非空字符串。除上述方法外，已识别的资源边界返回 `405 method_not_allowed`，未识别的路径返回 `404 not_found`。资源集合没有替换式写入，也没有隐式的服务进程操作。

### 1.2 Host 能力与授权边界

控制器默认从 Host 的扩展记录读取自己的启动选项；启动前会检查 Host API 兼容性和 Host 配置快照的读写能力。缺少该能力或 Host bridge 不可用时，管理请求分别以 `501 unsupported` 或 `503 unavailable` 结束。API key 是每个已启用传输的必需认证因素；授权能力不是按资源另设的公开 scope，也不会产生另一套公共 DTO。

HostRoute 的监听位置、是否被 Host 暴露为 HTTP，以及 Host 的本地部署策略均由 Host 负责。控制器不创建 Host 的 TCP/HTTP 地址，不能把 HostRoute 当作公网入口。

## 2. 传输与地址组合

### 2.1 逻辑路径与 HostRoute 组合

HTTP、Unix socket 和 gRPC 都直接使用上表的逻辑 `/v1/...` 路径。HostRoute 由 Host 把 handler 挂到配置的 `HostRoutePath`；实际请求路径是：

- `HostRoutePath == /v1` 时为 `/v1/...`；
- 否则为 `<HostRoutePath>/v1/...`。

例如 `HostRoutePath` 是 `/<host-prefix>` 时，逻辑的 global-settings 资源在 Host 中是 `/<host-prefix>/v1/global-settings`。`HostRoutePath` 不包含端口，也不是控制器自动生成的 base URL。handler 收到请求后会把这个挂载前缀还原为逻辑 `/v1` 路径，再进入共享 dispatcher。

HostRoute handler 由 Host 的 registration seam 注册，固定 handler ID 为 `nekolla.nekostick.controller.management`。正常的 HostRoute 配置使用 Host-owned 的 Prefix route；控制器不会绑定 socket 或 TCP listener。
`HostRoutePath` 必须是规范的绝对管理路径：长度为 `2..1024`，以 `/` 开头、不以 `/` 结尾，不含 `//`、`?`、`#`、NUL、CR 或 LF；单独的 `/` 不合法。HostRoutePath 只决定 Host 的 route 挂载位置，不改变资源的逻辑 `/v1` path。

### 2.2 自托管 HTTP/JSON

`HttpJson` 仅在显式启用并给出端口时启动：

- `http://127.0.0.1:<HttpPort>/v1/...`；
- `http://[::1]:<HttpPort>/v1/...`。

它是 loopback HTTP/1.1 明文连接，不是 HTTPS，也不会监听远程地址。请求头中的 `x-nekostick-controller-key` 携带 API key。

### 2.3 Unix-domain HTTP

`UnixSocket` 使用配置的绝对 Unix socket 路径承载 HTTP/1.1；URL authority 只是客户端库要求的占位符，socket 文件本身才是地址。例如客户端可以使用 Unix socket HTTP 客户端连接 `<absolute-socket-path>`，并在 HTTP header 中发送 API key。Windows 不支持此传输。

启动前最终 socket 路径必须不存在；不会覆盖已有文件、目录、symlink 或 reparse point。所有已存在的 parent directory 必须是实际目录、不能是 symlink/reparse point，且 group/other 不可写。`/tmp` 和 `/private/tmp`（以及解析后经过这些目录的路径）禁止作为 parent。绑定成功后文件模式被设置并验证为 `0600`；停止时只有在仍能证明路径、类型、模式和 parent chain 安全，且该 adapter 确实创建了该 socket 时才删除。无法证明所有权时宁可保留而不删除。

### 2.4 gRPC gateway

gRPC 只提供一个通用 unary gateway，不另定义资源 RPC：

```text
nekostick.controller.management.v1.ControllerManagement/Invoke
```

listener 是 loopback IPv4/IPv6 上的 HTTP/2 cleartext（h2c），地址为 `127.0.0.1:<GrpcPort>` 或 `[::1]:<GrpcPort>`；不使用 TLS 参数。`ControllerManagement.Invoke` 仅是通往上述 REST 资源面的网关，客户端必须把 REST method、逻辑 path、headers 和 JSON body 放入 `InvokeRequest`。

认证 key **只能**放在 gRPC metadata 的 `x-nekostick-controller-key` 中，不能放进 protobuf envelope。请求 envelope 中可以放 `content-type`、`if-match` 等普通管理 header，但不得出现认证 header。

## 3. 认证、请求 admission 与安全限制

### 3.1 API key

认证 header 名为 `x-nekostick-controller-key`，名称比较不区分大小写，值按精确匹配（内部使用定时安全比较）。配置的 key 必须长度 `32..4096` 个 UTF-16 字符（UTF-8 最多 `16 KiB`）且不得包含空白；HTTP、Unix 和 HostRoute 要求 header 中恰好一个 key 值；gRPC metadata 要求恰好一个同名的非 binary 值。重复、缺失、空值或不匹配的 key 对已通过 admission 的请求返回 `401 unauthorized`，不区分泄露具体失败原因。

控制器同时校验 transport 提交的 key 和 headers 中的 key，二者必须完全相同。key 不写入响应、状态、异常或日志，文档中的 `<API_KEY>` 仅为占位符，不是可用凭据。

### 3.2 有界 admission

| 项目 | 上限或规则 |
| --- | --- |
| 请求 body | `1 MiB`（`1,048,576` bytes）；HTTP 会读取到上限加 1 byte 以确认超限 |
| 响应 body | `1 MiB`；超限时使用 `response_too_large` 响应 |
| method | 非空，最多 32 个字符 |
| path | 非空，最多 8192 个字符，必须以 `/` 开头 |
| request header 名称 | 最多 64 个；每个名称最多 256 个字符；名称不可重复 |
| 一个 header 的值 | 最多 64 个；每个值最多 16 KiB |
| aggregate header | 所有名称和值的 UTF-8 字节总数最多 64 KiB |
| HTTP request line | Kestrel 上限 16 KiB；管理核心仍限制 path 为 8192 个字符 |
| gRPC envelope headers | 最多 63 个名称；第 64 个 header 名额保留给 metadata 转入的认证 header |
| gRPC 外层 message | receive/send 上限为 `1 MiB + 64 KiB + 32 KiB`；内层 body/header 仍按上表限制 |

query string 会随 path 传入核心并被拒绝；fragment 也不是管理 path。核心会删除末尾斜线后进行匹配，但客户端应直接使用规范的无末尾斜线路径。

### 3.3 JSON 请求规则

读取请求（GET）和无内容删除请求的 body 必须真正为空；不能发送 `{}` 或 `[]`。所有带 body 的写入都要求 body 非空、恰好一个 `Content-Type` header，并使用 `application/json`（允许 media type 参数，例如 `application/json; charset=utf-8`）。PATCH 还接受 `application/merge-patch+json`，见 §5。

管理 DTO 使用 camelCase 且大小写敏感。未知字段、注释、尾逗号、超过深度 32 的 JSON 和整数形式的 enum 都拒绝；enum 必须使用字符串名称。嵌入的 `metadataJson` 和 extension `settings` 也必须是合法 JSON，最大为 `1 MiB` UTF-8，并遵守深度 32、禁止注释和尾逗号的限制。

## 4. Canonical response envelope

### 4.1 结构

进入共享 dispatcher 的 application 响应使用 UTF-8 JSON，并通常带 `Content-Type: application/json; charset=utf-8`。成功 envelope 的形状如下（`data` 由具体资源定义）：

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

错误 envelope 保留全部字段，`data` 和 `version` 为 `null`，例如：

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

| 字段 | 类型 | 语义 |
| --- | --- | --- |
| `apiVersion` | integer | 固定为 `1` |
| `ok` | boolean | 成功为 `true`，错误为 `false` |
| `code` | string | 稳定的 application result code；客户端应按此分支 |
| `message` | string | 安全的固定人类可读消息；不应作为程序化分支条件 |
| `data` | object/array/null | 资源结果；204 没有 body |
| `version` | integer/null | 成功响应对应的 Host 配置快照聚合版本；错误通常为 `null` |

成功响应会在 header 中同时给出强 ETag，格式为带双引号的十进制版本，例如 `ETag: "42"`。资源 DTO 自身的 `version` 字段是各记录的服务器元数据，不替代这个聚合 ETag。

### 4.2 状态和结果码

| HTTP 状态 | envelope `code` | 含义 |
| ---: | --- | --- |
| 200 | `ok` | GET、PATCH 或 PUT 成功 |
| 201 | `ok` | POST 创建成功；另有 `Location` |
| 204 | （无 body） | DELETE 成功；响应仍带新聚合 ETag |
| 400 | `invalid_request` | JSON、header、body、ID、path 或资源语义无效 |
| 401 | `unauthorized` | API key 缺失、重复、为空或不匹配 |
| 404 | `transport_disabled` | 当前请求所用 transport 未启用 |
| 404 | `not_found` | 资源、ID 或 extension 记录不存在 |
| 405 | `method_not_allowed` | 已识别边界不支持该 HTTP method |
| 409 | `reserved_route` | 操作试图创建、修改或删除控制器保留 route |
| 412 | `precondition_failed` | If-Match 与当前聚合版本不匹配 |
| 428 | `precondition_required` | mutation 缺少恰好一个 If-Match |
| 501 | `unsupported` | Host API、配置能力或操作不支持 |
| 503 | `unavailable` | dispatcher、Host bridge 或运行时不可用 |
| 503 | `storage_unavailable` | Host 配置存储不可用 |
| 503 | `response_too_large` | canonical response 超过 `1 MiB` |

`If-Match` 缺失、格式错误、stale/CAS mismatch 的优先级和状态码见 §5；服务端不会把这些情况重试后再提交。HTTP/Unix 在 transport-level admission 失败时可能无法构造 canonical body：例如 body 超限、header 无效或 request construction 失败会直接返回静态空 body 的 `400`，也可能没有 `Content-Type`。不要对这种空 body 强行解析 envelope。HostRoute handler 的 admission rejection 使用共享 builder，通常返回 canonical `400` envelope。

### 4.3 gRPC 响应保持 HTTP 语义

对于可以构造并交付的 `InvokeResponse`（包括 gRPC metadata/admission rejection 和 dispatcher/application result），字段保持 transport-neutral 响应：

- `status_code` 等于同一响应的 HTTP-like status；
- `headers` 原样携带 canonical response headers（包括 `content-type`、ETag、Location）；
- `body` 是 canonical JSON envelope 的原始 UTF-8 bytes，204 时为空；
- `code` 只是 `success`、`invalid_request`、`unauthorized`、`transport_disabled`、`not_found`、`conflict`、`unsupported` 或 `unavailable` 等粗粒度 dispatch category，不能替代 `body` 内的 application `code`。

只有 protobuf framing、gRPC transport、连接断开或取消导致响应根本不能构造/交付时，才会是 raw gRPC failure；这类失败不存在可解析的 `InvokeResponse.body`。

## 5. 聚合 ETag、If-Match 与 JSON Merge Patch

### 5.1 一次性强前置条件

以下所有 mutation 都必须带**恰好一个**强 quoted aggregate `If-Match`：

- `PATCH /v1/global-settings`；
- `POST /v1/routes`、`PATCH/DELETE /v1/routes/{id}`；
- `POST /v1/services`、`PATCH/DELETE /v1/services/{id}`；
- `PUT/DELETE /v1/services/{id}/environment`；
- `PUT/DELETE /v1/extensions/{id}/settings`。

合法格式是 `If-Match: "<non-negative-decimal-version>"`，例如语法模板 `If-Match: "<VERSION>"`。不接受 `W/`、`*`、逗号列表、未加引号的数字、空字符串或带前导零的数字（`"0"` 除外）。多个 header value 也视为错误。

- 没有 If-Match：`428 precondition_required`；
- 不是恰好一个合法强 quoted 值：`400 invalid_request`；
- 值不是当前 Host 配置快照的聚合版本，或 Host 在 CAS 提交时发现版本已变化：`412 precondition_failed`；
- 服务端只执行一次 CAS，不替客户端重试，也不接受旧版本静默覆盖。

GET、PATCH、POST、PUT 的成功响应会给出读到或提交后的聚合 ETag；204 DELETE 也给出提交后的 ETag。`globalSettings.version`、route/service `version`、extension record `recordVersion` 和 extension settings `version` 只描述各 DTO 记录，不能拿来替代 ETag。

### 5.2 Merge Patch

三个对象资源的 PATCH（global-settings、route member、service member）按 JSON Merge Patch 处理：

1. body 必须是 JSON object，不能是数组、字符串、数字或 `null`；
2. `Content-Type` 可使用 `application/merge-patch+json`，实现也接受 `application/json`；
3. patch 的每个字段必须存在于对应 write DTO，未知字段立即 `400 invalid_request`；嵌套 object 也递归执行相同的已知字段检查；
4. 非 object 值替换字段，`null` 删除目标字段，object 递归合并，然后将完整结果按严格 DTO 规则重新解析；因此删除必需字段或造成类型/语义无效也会返回 `400`；
5. service PATCH 明确禁止顶层 `environment` 字段，环境必须走专用 subresource；
6. patch 不得携带 `id`、created/updated 时间、记录版本等只读字段，因为这些字段不在 write DTO 中。

PATCH 的 `If-Match` 仍是一次性的聚合版本，而不是被 PATCH 的记录版本。

## 6. 数据模型

JSON 字段名以本文代码样式为准（camelCase）。GUID 是字符串；时间是 ISO-8601 `DateTimeOffset`；毫秒字段是整数；数组可以为空。以下 read DTO 中的服务器字段可返回，但不能由写 DTO 设置。

### 6.1 根资源

`GET /v1` 的 `data`：

```json
{
  "version": 42,
  "globalSettings": { "...": "..." },
  "routes": [],
  "services": [],
  "extensions": []
}
```

根 `version`、envelope `version` 和 ETag 是同一个 Host 配置快照版本。`routes` 不包含控制器私有保留 route；`services` 不内嵌环境；`extensions` 只有 extension records，不内嵌 opaque settings。

### 6.2 Global settings

`globalSettings` read DTO 字段如下；PATCH 只使用不含 `version` 的 write DTO：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `version` | integer | global-settings 记录的服务器版本；不是 If-Match |
| `autoPortRangeStart`, `autoPortRangeEnd` | integer | 自动端口范围 |
| `maxRequestBodyBytes` | integer | Host 请求 body 上限 |
| `maxRequestHeaderBytes` | integer | Host 请求 header 上限 |
| `maxConcurrentRequests` | integer | 并发请求上限 |
| `requestReadTimeoutMs` | integer | 请求读取超时 |
| `configurationPollIntervalMs` | integer | 配置轮询间隔 |
| `trustedProxyCidrs` | string[] | 受信代理 CIDR |
| `proxyTimeouts` | object | `connectTimeoutMs`、`httpActivityTimeoutMs`、`httpTotalTimeoutMs`、`webSocketIdleTimeoutMs` |
| `clientIpRatePolicy` | object/null | `tokenLimit`、`tokensPerPeriod`、`replenishmentPeriodMs`、`queueLimit`、`rejectionBehavior`、`retryAfterBehavior` |
| `proxyRetries` | object | `maxRetries`、`initialBackoffMs`、`maximumBackoffMs`、`retryOnConnectionFailure`、`retryOnUpstreamDisconnect` |

`rejectionBehavior` 为 `Reject` 或 `Queue`；`retryAfterBehavior` 为 `None` 或 `FromReplenishmentPeriod`。global-settings PATCH 不允许额外的身份字段。

### 6.3 Route

route read DTO 字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | GUID string | 服务器分配的 route ID |
| `enabled` | boolean | 是否启用 |
| `matcher` | object | 见下方 matcher |
| `target` | object | 见下方 target |
| `priority` | integer | route 优先级 |
| `forwarding` | object | `mode` 和可空 `replaceTemplate` |
| `requestHeaderRewrites` | object[] | 请求 header 改写列表 |
| `responseHeaderRewrites` | object[] | 响应 header 改写列表 |
| `metadataJson` | string | 扩展拥有的合法 JSON 文本 |
| `createdAt`, `updatedAt` | timestamp | 服务器时间 |
| `version` | integer | route 记录版本；不是 If-Match |
| `clientIpRatePolicy` | object/null | route 级 rate policy |
| `maxRequestBodyBytes`, `maxRequestHeaderBytes` | integer/null | route 覆盖值 |
| `maxConcurrentRequests` | integer/null | route 覆盖值 |
| `requestReadTimeoutMs` | integer/null | route 覆盖值 |
| `proxyRetries` | object/null | route 级 retry 覆盖值 |

route create/PATCH write DTO 使用上述可变字段，但不含 `id`、时间和 `version`。`matcher` 的字段为 `type`、`pattern`、`hostPatterns`、`methods`；`type` 是 `Exact`、`ExactCaseInsensitive`、`Prefix`、`PrefixCaseInsensitive` 或 `Regex`。`target.type` 是：

- `Microservice`：只提供 `serviceId`；
- `StaticFile`：只提供 `rootPath`；
- `ExtensionHandler`：只提供 `handlerId`。

不属于所选 target type 的字段必须为 `null`。forwarding mode 是 `Preserve`、`Strip` 或 `Replace`。每个 header rewrite 为 `operation`（`Remove`、`Set` 或 `Add`）、`name` 和可空 `value`。`metadataJson` 必须是合法 JSON 文本。

### 6.4 Service

service read DTO 字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | GUID string | 服务器分配的 service ID |
| `enabled` | boolean | 是否启用 |
| `fileName` | string | 可执行文件路径 |
| `argumentList` | string[] | 进程参数 |
| `workingDirectory` | string | 工作目录 |
| `startMode` | enum | `Eager` 或 `Lazy` |
| `restartPolicy` | enum | `Never`、`OnFailure` 或 `Always` |
| `healthCheck` | object | `type`、可空 `httpPath`、`timeoutMs` |
| `createdAt`, `updatedAt` | timestamp | 服务器时间 |
| `version` | integer | service 记录版本；不是 If-Match |

service create/PATCH write DTO 使用可变字段；create 可选地接收 `environment` map，但 service read DTO 永远不包含该字段。service PATCH 若出现 `environment` 会被拒绝；请使用 §7 的 environment subresource。

`healthCheck.type` 是 `Process`、`Tcp` 或 `Http`。服务 ID path 必须是单个可解析 GUID segment。

### 6.5 Extension records 与 opaque settings

extension record 是只读记录，字段为：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `extensionId` | string | Host extension ID |
| `version` | string | extension 版本字符串 |
| `loadState` | enum | `Discovered`、`Loaded`、`Stopped`、`Failed` 或 `Unloading` |
| `createdAt`, `updatedAt` | timestamp | 记录时间 |
| `recordVersion` | integer | record 版本；不是 If-Match |

extension settings 是 extension 自己拥有的 opaque JSON 值，不是控制器内部 DTO。read DTO 为：

```json
{
  "extensionId": "<extension-id>",
  "schemaVersion": 1,
  "settings": {},
  "version": 7
}
```

write body 只有 `schemaVersion` 和 `settings`；extension identity 由 URL path 决定。`settings` 可以是任意合法 JSON value（通常为 object），但受 §3.3 的大小、深度和语法限制。控制器不解释其中的业务字段；未知字段也不能被当作控制器字段提交到 write DTO。

## 7. REST 资源操作

本节的 method/path/body 对 HTTP、Unix 和 HostRoute 相同；gRPC 只是在 `InvokeRequest` 中传递相同的值。以下状态是 application-level 成功状态；通用认证、admission、If-Match、Host bridge 错误按 §3–§5 映射。

### 7.1 `GET /v1`

body 必须为空。成功返回 `200`，`data` 为 §6.1 根资源，带聚合 ETag。根结果隐藏控制器私有保留 route、服务环境和 extension settings。

### 7.2 `GET/PATCH /v1/global-settings`

- `GET`：body 为空，返回 `200`，`data` 为 §6.2 read DTO，带读快照的聚合 ETag。
- `PATCH`：body 为 JSON Merge Patch，必须带一个聚合 If-Match；成功返回 `200`，`data` 为更新后的 read DTO，带新的聚合 ETag。

### 7.3 `GET/POST /v1/routes`

- `GET`：body 为空，返回 `200` 和 read DTO 数组；保留 route 不出现在数组中，ETag 对应读快照。
- `POST`：body 为 route write DTO，必须带 If-Match；成功创建服务器 GUID，返回 `201`、read DTO、新聚合 ETag，以及 `Location: /v1/routes/<new-id>`。Location 使用逻辑 canonical path；通过 HostRoute 使用时由 Host 挂载前缀解析。

### 7.4 `GET/PATCH/DELETE /v1/routes/{id}`

- `GET`：body 为空，返回 `200` 和单个 route read DTO；私有保留 route 对外表现为 `404 not_found`。
- `PATCH`：body 为 route Merge Patch，必须带 If-Match；返回 `200` 和更新后的 read DTO、新聚合 ETag。
- `DELETE`：body 必须为空，必须带 If-Match；返回 `204`、空 body 和新聚合 ETag。

对保留 route 的 PATCH/DELETE 返回 `409 reserved_route`；新 route 的 target 或 matcher 试图占用保留边界时，POST/PATCH 同样返回该结果。

### 7.5 `GET/POST /v1/services`

- `GET`：body 为空，返回 `200` 和 service read DTO 数组；环境不会嵌入结果，ETag 对应读快照。
- `POST`：body 为 service write DTO，必须带 If-Match；可包含一次性的环境 map。成功返回 `201`、不含环境的 service read DTO、新聚合 ETag，以及 `Location: /v1/services/<new-id>`。

### 7.6 `GET/PATCH/DELETE /v1/services/{id}`

- `GET`：body 为空，返回 `200` 和 service read DTO；不返回 environment。
- `PATCH`：body 为 service Merge Patch，必须带 If-Match；顶层 environment 字段无效，成功返回 `200` 和更新后的 service read DTO、新聚合 ETag。
- `DELETE`：body 必须为空，必须带 If-Match；返回 `204`、空 body 和新聚合 ETag。

### 7.7 `GET/PUT/DELETE /v1/services/{id}/environment`

这是唯一读取或修改 service environment 的公共 subresource；环境值可能是 secret，客户端必须按敏感资料处理。

- `GET`：body 为空，返回 `200`：

  ```json
  {
    "serviceId": "<service-id>",
    "environment": {}
  }
  ```

  body 和响应日志中不要填入或复制实际环境值。响应带读快照聚合 ETag。

- `PUT`：body 必须是 `{"environment": {"<name>": "<value>"}}` 形式的 JSON object，必须带 If-Match；成功返回 `200` 和同样形状的 environment read DTO、新聚合 ETag。文档示例中的 `<name>`、`<value>` 仅为占位符，不代表 secret。
- `DELETE`：body 必须为空，必须带 If-Match；成功清空环境并返回 `204`、空 body 和新聚合 ETag。

未知 service ID 返回 `404 not_found`；环境对象不能通过 service PATCH 旁路修改。

### 7.8 `GET /v1/extensions` 与 `GET /v1/extensions/{id}`

body 必须为空。集合返回 `200` 和 extension record read DTO 数组；member 返回 `200` 和一个 read DTO。两者只返回 §6.5 的只读 record，不允许写入 record，也不把 opaque settings 内嵌到 record。响应带读快照聚合 ETag。

### 7.9 `GET/PUT/DELETE /v1/extensions/{id}/settings`

- `GET`：body 为空；只有当 extension record 存在且有 settings record 时返回 `200` 和 §6.5 opaque settings read DTO。否则返回 `404 not_found`。
- `PUT`：body 为 `{"schemaVersion": <integer>, "settings": <任意合法 JSON value>}`，必须带 If-Match；extension record 必须存在。成功返回 `200`、更新后的 opaque settings read DTO 和新的聚合 ETag；没有既有 settings 时会在该 extension 下创建。
- `DELETE`：body 必须为空，必须带 If-Match；extension record 和 settings record 都必须存在。成功删除 settings，返回 `204`、空 body 和新的聚合 ETag。

settings DTO 中的 `version` 是 settings 记录版本；它不改变本节 mutation 一律使用聚合 If-Match 的规则。

## 8. gRPC wire contract

对应 proto 的请求和响应字段如下：

```proto
service ControllerManagement {
  rpc Invoke(InvokeRequest) returns (InvokeResponse);
}

message Header {
  string name = 1;
  repeated string values = 2;
}

message InvokeRequest {
  string method = 1;
  string path = 2;
  repeated Header headers = 3;
  bytes body = 4;
}

message InvokeResponse {
  int32 status_code = 1;
  string code = 2;
  repeated Header headers = 3;
  bytes body = 4;
}
```

- `method` 使用 REST method，例如 `GET`、`POST`、`PATCH`、`PUT`、`DELETE`；
- `path` 使用逻辑 `/v1/...` path，不填 gRPC 完整方法名；
- `headers` 放 `content-type`、`if-match` 等普通 header；认证 key 不得放入这里；
- `body` 是 canonical JSON 请求的 UTF-8 bytes；GET 和 DELETE 使用空 bytes；
- `InvokeResponse.body` 是 §4 的 canonical JSON envelope bytes，不能只读取 `InvokeResponse.code`；
- 原生 protobuf 客户端直接写入 bytes。采用 grpcurl JSON 映射时，bytes 由客户端按 base64 表示，但那只是 wire 映射，不是把 JSON 对象嵌入 protobuf。

metadata 中 key 不存在或不匹配时，gRPC 返回带 `401` 和 canonical unauthorized envelope 的 `InvokeResponse`；envelope 中出现认证 header 时，返回 `400 invalid_request`。可交付的 application status、headers 和 body 必须与 HTTP-like 响应保持一致。

## 9. HostRoute 保留边界与零监听器 bootstrap

### 9.1 私有 controller route

HostRoute 的 Prefix route 由控制器使用 handler ID `nekolla.nekostick.controller.management` 组合。控制器将其视为私有保留边界：

- 根列表和 routes 列表过滤该 route；
- 按该 route ID GET 时返回 `404 not_found`；
- 创建或修改会匹配 `HostRoutePath`（或其子路径）的 route，或使用 controller handler ID 的 route target，返回 `409 reserved_route`；
- 删除当前保留 route 也返回 `409 reserved_route`。

保留判断同时检查 handler identity 和 HostRoutePath 相关 matcher，不能通过改变 route DTO 的其他字段绕过。HostRoute 的实际注册和本地性仍由 Host 负责。

### 9.2 全部外部 listener 关闭时

当 Host 读到的选项将 HostRoute、HTTP、gRPC、Unix 全部关闭时，entrypoint 不把该状态当作可交付凭据的普通停用状态，而是建立一次临时 bootstrap：

1. 在内存中生成 CSPRNG credential（48 个随机 bytes 的 base64 表示），不写回 Host 记录；
2. 在内存中生成随机 HostRoutePath，格式为 `/controller` 加八位十进制随机后缀；不使用固定或文档中的实际路径；
3. 临时启用 HostRoute，并使用固定 controller handler ID 和 Host 配置快照读写能力；
4. 在 Host 配置快照中创建带内部 ownership marker 的 Prefix route。marker 只用于证明该 route 属于本 controller bootstrap 实例，文档不展示其实际值，也不把 credential 写进 marker；
5. 注册 handler 后，先清理同一 controller/handler 下带合法 `bootstrap-v1:` ownership marker 的陈旧 bootstrap routes，再创建当前 route。

停止或启动回滚时，控制器只在以下所有证明仍匹配时删除临时 route：精确的 route ID 和 canonical path、Prefix matcher、固定 handler ID，以及当前 bootstrap ownership marker。证明不匹配时拒绝删除，避免误删 Host 或用户后来接管的 route。陈旧 marker route 的清理同样只针对本 controller handler 和 marker 格式，不会按路径盲删普通 route。

### 9.3 credential 交付限制

当前 Host logger/status ABI 只接受安全的类别代码，不能安全承载 bootstrap credential。entrypoint 中的 secret-delivery hook 因而是**有意的空实现 TODO**：当前构建不会打印、记录、发送或以响应形式发出该 secret。不得把健康状态、route marker、文档占位符或随机 path 当作 credential，也不得声称该启动流程已经向用户显示 secret。没有未来的用户授权交付通道时，临时 route 的 credential 对外不可得；这是当前实现的明确限制。

## 10. 安全使用要点

- 所有自托管 listener 只能绑定 loopback；HostRoute 必须由 Host 保持在本地边界内。
- HTTP/Unix 使用 header key，gRPC 使用 metadata-only key；任何 protobuf envelope 中的 key 都拒绝。
- 不在日志、shell history、错误报告或示例中记录 API key、environment value 或其他敏感 JSON。
- 写入始终先取得最新 GET 的 ETag，再用一次强 quoted If-Match；收到 `412` 时由调用方重新读取并作业务决策，服务端不会自动重试旧写入。
- Unix socket 使用非公共、可信 parent chain 和 `0600`；不要以公共临时目录替代权限检查。
- HostRoute private marker、handler ID 和随机 path 不是认证材料；认证仍要求 API key。
- 仅依赖稳定的 HTTP status、envelope `code`、`ok` 和结构化 `data`；不要依赖固定 `message` 文本或 gRPC 粗粒度 `code` 做资源分支。
