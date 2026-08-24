# Nekolla.Nekostick.Controller

`Nekolla.Nekostick.Controller` 是一个面向同机管理的 Nekostick 扩展。它把 Host 配置管理和本地服务运行状态整理成统一的管理 API, 并通过多种本地传输提供访问。

控制器只服务本机管理场景。HTTP 与 gRPC 只绑定 loopback; Unix socket 使用受信任的本地路径和 `0600` 权限; 所有启用的传输都使用同一个 API key。

## 功能概览

- 管理 Host 的路由、服务、全局设置和 extension settings
- 读取服务的 runtime telemetry
- 查看 controller 当前的 listener/bootstrap 状态
- 热重载 controller settings, 包括 listener/port/path 和 API key
- 使用统一 JSON envelope、聚合 ETag 和强 `If-Match` 处理配置并发
- 支持 HostRoute、loopback HTTP/JSON、loopback gRPC 和 Unix-domain socket

RouteEvents 的 observation、hook、stream、callback 和相关 API 不在此扩展支持范围内。

## 运行要求

- .NET 10
- Host API `>=1.3.0 <2.0.0`
- Extension ID: `nekolla.nekostick.controller`
- Entry assembly: `Nekolla.Nekostick.Controller.dll`
- Entry type: `Nekolla.Nekostick.Controller.ControllerEntrypoint`

部署、发现和 HostRoute 的对外暴露方式由 Host 决定。控制器不会创建远程 TCP 地址。

## 基本使用

控制器从 Host 的 extension settings 读取配置。最小 HTTP 配置示例:

```json
{
  "loopbackOnly": true,
  "enableHttpJson": true,
  "httpPort": 48123,
  "apiKey": "<至少 32 个字符且不含空白的密钥>",
  "apiScope": "FullConfiguration"
}
```

也可以启用 HostRoute、gRPC 或 Unix socket。启动配置四个 listener 全部关闭时，controller 会创建临时 HostRoute，并通过 Host API 1.3 的 host-attributed LogWriter 一次性交付本次 bootstrap path 和 API key。

启用 HTTP 后, 所有请求都需要同一个认证 header:

```http
x-nekostick-controller-key: <API_KEY>
```

示例:

```bash
curl --fail-with-body \
  --header "x-nekostick-controller-key: $API_KEY" \
  "http://127.0.0.1:48123/v1"
```

配置更新需要先读取当前 ETag, 再提交强 `If-Match`; runtime telemetry 和 controller state 是只读运行状态, 不参与配置 ETag。完整资源矩阵、字段、错误码、传输细节和安全限制见 [docs/controller-api.md](docs/controller-api.md)。

## 安全要点

- API key 长度 `32..4096`, 不能包含空白; 缺失、重复、弱值、超长或不匹配都会失败。
- 热重载成功后新 API key 立即生效, 旧 key 立即失效。
- environment value 可能包含 secret, 应按敏感资料处理。
- HTTP 与 gRPC 仅 loopback; Unix socket 必须满足父目录和 `0600` 限制。
- controller 私有 HostRoute 会隐藏并防止公共 route 操作占用。
- 客户端应按 status、envelope `code` 和结构化 `data` 分支, 不要依赖固定 `message` 文案。

## 构建

```bash
dotnet build Nekolla.Nekostick.Controller.slnx --nologo -v:q
```

项目以 warnings-as-errors 构建, 并为公共 API 生成 XML 文档。

## API 文档

完整的 API endpoints、请求/响应规则、ETag/If-Match 语义、传输行为、错误码和安全限制见 [docs/controller-api.md](docs/controller-api.md)。
