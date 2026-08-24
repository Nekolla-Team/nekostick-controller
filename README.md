# Nekolla.Nekostick.Controller

`Nekolla.Nekostick.Controller` 是 Nekostick 的同机管理扩展。它通过 HostRoute、loopback HTTP/JSON、loopback gRPC 和 Unix-domain socket 提供同一套资源 API，用来查看并更新 Host 配置，以及读取服务运行状态。

控制器只面向本机管理。HTTP 和 gRPC 只绑定 loopback；Unix socket 使用受信任的本地路径和 `0600` 权限；所有启用的传输都要求同一个 API key。

## 运行要求

- .NET 10
- Host API `>=1.3.0 <2.0.0`
- Extension ID: `nekolla.nekostick.controller`
- Entry assembly: `Nekolla.Nekostick.Controller.dll`
- Entry type: `Nekolla.Nekostick.Controller.ControllerEntrypoint`

部署、发现和 HostRoute 的对外暴露方式由 Host 决定。控制器自身不会创建远程 TCP 地址。

## 快速上手

### 1. 配置传输

控制器从 Host 的 extension settings 读取选项。一个最小 HTTP 配置包含：

```json
{
  "loopbackOnly": true,
  "enableHttpJson": true,
  "httpPort": 48123,
  "apiKey": "<至少 32 个字符且不含空白的密钥>",
  "apiScope": "FullConfiguration"
}
```

`apiScope` 当前只有 `FullConfiguration` 可用。也可以启用 `enableHostRoute`、`enableGrpc` 或 `enableUnixSocket`。四个 listener 全部关闭时，控制器会创建一个临时 HostRoute，详见 [Bootstrap 凭据](#bootstrap-凭据)。

### 2. 请求管理 API

HTTP 与 Unix 传输使用同一个 header：

```http
x-nekostick-controller-key: <API_KEY>
```

读取 API 根资源：

```bash
curl --fail-with-body \
  --header "x-nekostick-controller-key: $API_KEY" \
  "http://127.0.0.1:48123/v1"
```

成功响应是统一 envelope；持久化配置资源会同时返回聚合 `ETag`：

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
ETag: "42"
```

```json
{
  "apiVersion": 1,
  "ok": true,
  "code": "ok",
  "message": "The operation completed.",
  "data": {
    "version": 42,
    "globalSettings": {},
    "routes": [],
    "services": [],
    "extensions": []
  },
  "version": 42
}
```

### 3. 做一次条件更新

持久化配置的所有 mutation 都要求恰好一个强 `If-Match`。典型流程是先读，再带着读取到的 ETag 写入：

```bash
curl --fail-with-body \
  --request PATCH \
  --header "x-nekostick-controller-key: $API_KEY" \
  --header "Content-Type: application/merge-patch+json" \
  --header 'If-Match: "42"' \
  --data '{"enabled": false}' \
  "http://127.0.0.1:48123/v1/services/<SERVICE_ID>"
```

如果返回 `412 precondition_failed`，先重新 `GET` 最新版本，再由调用方决定是否重试。服务端不会替客户端自动重试旧版本。

### 4. 查看服务运行状态

```bash
curl --fail-with-body \
  --header "x-nekostick-controller-key: $API_KEY" \
  "http://127.0.0.1:48123/v1/services/<SERVICE_ID>/runtime"
```

runtime telemetry 是只读快照，不参与配置 ETag 或 `If-Match`。成功 envelope 的 `version` 固定为 `null`，响应不带 ETag：

```json
{
  "apiVersion": 1,
  "ok": true,
  "code": "ok",
  "message": "The operation completed.",
  "data": {
    "serviceId": "<SERVICE_ID>",
    "processId": 1234,
    "startedAt": "2026-08-24T01:23:45.0000000+00:00",
    "uptimeMs": 123456,
    "lifecycleState": "Running",
    "healthState": "Healthy",
    "forwardedRequestCount": 1200,
    "activeForwardedRequestCount": 3,
    "lastUpdatedAt": "2026-08-24T01:25:00.0000000+00:00",
    "lastHealthAt": "2026-08-24T01:24:59.0000000+00:00"
  },
  "version": null
}
```

Host 没有该服务的 telemetry 时返回 `404 not_found`；能力不支持返回 `501 unsupported`；运行时/存储不可用返回 `503 storage_unavailable`。

## 资源一览

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

写入只接受资源自己的可变字段。ID、时间戳和记录版本都由服务器拥有；未知字段会被拒绝。普通 service 响应不内嵌 environment；environment 只能通过专用 subresource 访问。

RouteEvents 的 observation、hook、stream、callback 和相关 API 不在此扩展支持范围内。

## 传输

### HostRoute

HostRoute 由 Host 挂载到配置的 `hostRoutePath`。Host 决定它如何被 HTTP 暴露；控制器不创建公网入口。

请求路径规则：

- `hostRoutePath == /v1`：使用 `/v1/...`；
- 其他路径：使用 `<hostRoutePath>/v1/...`。

控制器的 HostRoute 基础设施是私有保留 route，不能通过公共 route 资源创建、修改或删除。

### HTTP/JSON

`enableHttpJson` 启用后只监听 loopback：

```text
http://127.0.0.1:<httpPort>/v1/...
http://[::1]:<httpPort>/v1/...
```

这是本机 HTTP/1.1 明文，不提供 HTTPS。

### gRPC

gRPC 提供一个通用 unary gateway：

```text
nekostick.controller.management.v1.ControllerManagement/Invoke
```

客户端把 REST method、逻辑 `/v1/...` path、普通 headers 和 JSON body 放进 `InvokeRequest`。API key 只能放在 gRPC metadata 的 `x-nekostick-controller-key`，不能放进 protobuf envelope。响应保留与 HTTP 相同的 status、headers 和 canonical JSON body。

### Unix-domain socket

Unix socket 承载 HTTP/1.1，仅在非 Windows 平台可用。配置使用绝对路径；已有文件不会被覆盖。父目录必须是安全的真实目录：不允许 symlink/reparse point，不允许 group/other 可写，也不能经过 `/tmp` 或 `/private/tmp`。socket 文件模式固定为 `0600`。

## Bootstrap 凭据

如果配置里四个 listener 都关闭，控制器会在当前启动实例中临时启用 HostRoute，并生成随机路径和 API key。成功创建私有 route 后，它会通过 Host API 1.3 的 host-attributed LogWriter 一次性交付该凭据。

这条信息只在当前 startup generation 输出一次，只包含本次 ephemeral route path 和 API key，不写入持久化设置、响应或异常。正常配置的 API key 永远不会被记录。缺少 Host API 1.3 的 LogWriter 会使启动失败。

## 安全要点

- API key 长度 `32..4096`，不能包含空白；缺失、重复、弱值、超长或不匹配都会失败。
- 配置 mutation 使用一次性聚合 `If-Match`；runtime telemetry 不接受 `If-Match`。
- environment value 可能包含 secret，应按敏感资料处理。
- HTTP 与 gRPC 仅 loopback；Unix socket 必须满足父目录和 `0600` 限制。
- HostRoute 私有 route 会隐藏并防止公共 route 操作占用。
- 依赖稳定 status、envelope `code` 和结构化 `data`；不要依赖固定 `message` 文案做分支。

## 构建

```bash
dotnet build Nekolla.Nekostick.Controller.slnx --nologo -v:q
```

项目以 warnings-as-errors 构建，并为公共 API 生成 XML 文档。

## 完整 API 参考

资源字段、请求/响应规则、ETag 语义、传输细节、错误码和安全限制见 [docs/controller-api.md](docs/controller-api.md)。
