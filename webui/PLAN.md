# Nekostick Controller WebUI 规划 (webui/)

对接 `nekolla.nekostick.controller` 管理 API (`apiVersion: 1`) 的本地管理前端。后端协议见 `../docs/controller-api.md`。

## 1. 目标与非目标

### 目标

- 提供 Route / Service / Global Settings / Extension Settings 的查看与管理界面
- 正确处理统一 JSON envelope、强 ETag `If-Match` CAS、JSON Merge Patch
- 展示 service runtime telemetry 并周期刷新
- 支持 controller state 查看与 settings 热重载
- 纯静态 SPA 产物 (`vite build`)，可由任意静态服务器或 Host 托管

### 非目标

- 不实现 RouteEvents observation/hook/stream/callback 相关功能（后端不支持）
- 不做服务端渲染、SSR 或独立后端中间层
- 不把 API key 输出到日志、URL、错误报告或控制台；key 持久化仅使用 localStorage

## 2. 技术栈

| 层 | 选型 | 说明 |
| --- | --- | --- |
| 框架 | Vue 3 + `<script setup>` + TypeScript | Composition API |
| 构建 | Vite | 纯静态 SPA 输出 |
| UI | Naive UI | 内置暗色主题、TS 友好 |
| 数据层 | TanStack Query v5 | 缓存/轮询/失效重取 |
| 路由 | vue-router v4 | hash history（纯静态托管免服务端 rewrite） |
| 客户端状态 | 模块级 `reactive` store（不引入 Pinia） | 仅保存 API base URL 与 key |

包管理器：pnpm。语言：TypeScript strict。

## 3. 目录结构

```
webui/
├── PLAN.md
├── .gitignore
├── index.html
├── package.json
├── tsconfig.json
├── vite.config.ts
├── src/
│   ├── main.ts
│   ├── App.vue
│   ├── router/index.ts            # hash history 路由表
│   ├── api/
│   │   ├── types.ts               # envelope + 各资源 DTO 类型
│   │   ├── client.ts              # fetch 封装: base URL / key header / envelope 解析
│   │   ├── etag.ts                # 聚合版本缓存与 If-Match 注入
│   │   └── resources/
│   │       ├── root.ts            # GET /v1
│   │       ├── controller.ts      # state / reload-settings
│   │       ├── globalSettings.ts
│   │       ├── routes.ts
│   │       ├── services.ts        # 含 environment / runtime 子资源
│   │       └── extensions.ts      # 含 settings 子资源
│   ├── stores/
│   │   └── connection.ts          # baseURL + apiKey, localStorage 持久化 + 更换入口
│   ├── composables/
│   │   └── useCas.ts             # "读最新版本 → 带 If-Match 写入 → 412 处理" 流程
│   ├── views/
│   │   ├── ConnectView.vue        # 首次配置与更换 key / base URL
│   │   ├── DashboardView.vue      # controller state + reload-settings + 配置概览
│   │   ├── RoutesView.vue         # route 列表 / 创建 / 编辑 / 删除
│   │   ├── ServicesView.vue       # service 列表 / CRUD / environment 编辑
│   │   ├── ServiceRuntimeView.vue # telemetry 详情, 轮询刷新
│   │   ├── ExtensionsView.vue     # extension 列表 + settings JSON 编辑
│   │   └── GlobalSettingsView.vue # 全局设置表单
│   └── components/                # 表单控件、envelope 错误提示、状态徽标、BootstrapBanner 等
└── tests/                         # Vitest: etag/CAS/client 单测
```

## 4. API 对接设计

### 4.1 传输与认证

- 默认 base URL 为当前 origin（同源 HostRoute 场景）；ConnectView 可覆盖为 `http://127.0.0.1:<port>`
- 所有请求带 `x-nekostick-controller-key: <key>`；gRPC 传输不从前端使用
- HostRoute 前缀规则：逻辑路径 `/v1/...` 拼接在用户配置的前缀之后（前缀为 `/v1` 时不重复拼接）
- API key 与 base URL 持久化在 localStorage，跨会话保留；顶栏常驻「连接设置」入口，可随时更换 key 或地址，保存后立即生效并 `queryClient.clear()`
- key 只存在于内存 store 与 localStorage，绝不写入日志、URL 或错误上报

### 4.2 Envelope 处理 (`client.ts`)

- 成功：解析 `{ ok, code, data, version }`，把响应头 `ETag` 与 `version` 一并返回给调用方
- 错误：按 HTTP status + envelope `code` 分支，不依赖 `message` 文案
- admission 层空 body `400`（无 canonical envelope）：检测空 body 时降级为通用传输错误，不强行解析
- `401 unauthorized` → 清除连接状态并跳转 ConnectView
- `404 transport_disabled` 与普通 `404 not_found` 区分提示
- `503 unavailable` / `storage_unavailable` / `response_too_large` 分别映射为可读错误

### 4.3 ETag / CAS 流程 (`etag.ts` + `useCas.ts`)

- 模块级缓存：资源路径 → 最近一次读到的聚合版本（quoted strong ETag 原样保存）
- 所有持久化配置 mutation（reload-settings、global-settings PATCH、route/service POST/PATCH/DELETE、environment PUT/DELETE、extension settings PUT/DELETE）必须携带恰好一个 `If-Match`
- PATCH 使用 `Content-Type: application/merge-patch+json`，body 必须是 object，`null` 表示删除字段，数组整体替换
- 收到 `412 precondition_failed`：使对应查询失效并重取，UI 提示冲突后由用户重试；不自动盲重试
- 收到 `428 precondition_required` / `400 invalid_request`（If-Match 格式类）：视为客户端 bug，记录并修复缓存逻辑

### 4.4 TanStack Query 约定

| Query Key | 内容 | 轮询 |
| --- | --- | --- |
| `['root']` | GET /v1 概览 | 关闭 |
| `['controller', 'state']` | bootstrap/listener 状态 | 5s |
| `['routes']` / `['routes', id]` | route 列表/详情 | 关闭 |
| `['services']` / `['services', id]` | service 列表/详情 | 关闭 |
| `['services', id, 'environment']` | environment map | 关闭 |
| `['services', 'runtime']` / `['services', id, 'runtime']` | telemetry snapshot | 3s，页面不可见时暂停 |
| `['extensions']` / `['extensions', id]` | extension record | 关闭 |
| `['extensions', id, 'settings']` | settings JSON | 关闭 |
| `['global-settings']` | 全局设置 | 关闭 |

- mutation 成功后按受影响范围 `invalidateQueries`；写响应自带新聚合版本时直接写入 etag 缓存再失效，减少一次往返
- runtime/state 响应 `version === null` 且无 ETag，不进入 CAS 缓存

### 4.5 Bootstrap 横幅与自动重连

- App.vue 顶层挂载全局 `BootstrapBanner`：`['controller', 'state']` 轮询返回 `bootstrapMode === true` 时显示显眼警告横幅（warning 配色、不可关闭），提示控制器正以临时 HostRoute + 随机一次性 key 运行，引导前往连接设置更换正式 key、前往 Global Settings 完成正式配置；切回 configured mode 后自动消失
- reload-settings 成功后：把响应中的新聚合版本写入 etag 缓存 → 重取 controller state 与根概览；listener/端口变更可能使当前 base URL 失效，按下方重连流程处理
- 连接层自动重连：请求网络错误或 `503 unavailable` 时按指数退避静默重试（有上限）；state 轮询一旦恢复成功即 `invalidateQueries()` 全量刷新并清除错误提示
- 当前传输被关闭（持续失败或收到 `404 transport_disabled`）时：顶部显示「连接丢失」横幅并提供跳转连接设置的入口，不自动清空已保存的 key

## 5. 页面规划

1. **ConnectView**：首次配置与更换 key 的表单；base URL 与 key 均持久化到 localStorage，下次打开自动复用；保存后立即以 GET /v1 验证并反馈结果
2. **DashboardView**：bootstrapMode 与四类 listener 的 enabled/running 徽标；「Reload Settings」按钮走 If-Match CAS 流程，成功后按 4.5 流程自动重读 state 并应对端口变化；根概览的 version/routes/services/extensions 计数
3. **RoutesView**：列表（matcher/target/priority/enabled）；创建与编辑用分步表单（matcher.type 五选一、target.type 三选一、forwarding.mode 三选一）；删除需确认
4. **ServicesView**：列表 + CRUD；startMode/restartPolicy/healthCheck(type 三种) 表单；environment 用独立 KV 编辑器（PUT 整体替换 / DELETE 清空），value 按 secret 处理（密码型输入、不回显日志）
5. **ServiceRuntimeView**：lifecycleState/healthState 徽标、uptimeMs 人性化显示、请求计数；3s 轮询
6. **ExtensionsView**：record 只读列表（loadState 徽标）；settings 以 JSON 编辑器呈现，PUT 提交前本地校验 object 形状与深度 ≤ 32
7. **GlobalSettingsView**：端口范围、限制值、proxyTimeouts/proxyRetries 表单；PATCH merge-patch 只提交被修改的字段

所有写操作统一错误呈现：status + `code` + 后端 message，`412` 附「配置已被他人修改」文案。
所有页面共享的全局行为（App.vue 层）：BootstrapBanner 横幅、连接丢失横幅、顶栏「连接设置」入口，详见 4.5。

## 6. 开发与构建

- `pnpm dev`：Vite dev server，`server.proxy` 把 `/v1` 反代到 `http://127.0.0.1:<httpPort>`，便于联调 loopback HTTP 传输
- `pnpm test`：Vitest 覆盖 client envelope 解析、etag 缓存、CAS 412 分支、merge-patch 构造
- TypeScript strict；不使用 `any` 断言后端 DTO

## 7. 里程碑

1. **M1 骨架**：Vite + Vue + Naive UI + Router + Query 初始化；connection store 与 ConnectView
2. **M2 API 层**：types / client / etag / useCas 全部落地并通过单测
3. **M3 只读页面**：Dashboard、Routes/Services/Extensions 列表、Runtime 轮询、BootstrapBanner 横幅
4. **M4 写路径**：各资源 CRUD、environment、extension settings、Global Settings PATCH
5. **M5 打磨**：错误分支全覆盖、暗色主题、build 产物冒烟验证

每个里程碑完成即可独立验证；M2 是后续一切写操作的正确性地基。
