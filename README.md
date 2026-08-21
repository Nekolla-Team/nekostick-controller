# Nekolla.Nekostick.Controller

`Nekolla.Nekostick.Controller` is a .NET 10 Nekostick extension that provides a local management API for the host's persisted configuration. It is intended for same-machine administration and exposes one resource-oriented management contract through multiple local transports.

The extension supports a Host-owned route, loopback HTTP/JSON, loopback gRPC, and a Unix-domain socket transport. All transports share the same request dispatcher, authentication rules, resource model, concurrency semantics, and response format.

## Compatibility

- **Target framework:** `net10.0`
- **Required Host API:** `>=1.2.0 <2.0.0`
- **Extension ID:** `nekolla.nekostick.controller`
- **Entry assembly:** `Nekolla.Nekostick.Controller.dll`
- **Entry type:** `Nekolla.Nekostick.Controller.ControllerEntrypoint`

The extension is loaded by the Nekostick host according to `src/Nekolla.Nekostick.Controller/manifest.json`. Deployment and extension discovery remain host-specific.

## Capabilities

The controller provides:

- A Host API 1.2 resource-oriented management surface.
- Full-configuration read/replace as an internal persistence seam, while preserving unrelated configuration members during public mutations.
- Aggregate optimistic concurrency using strong ETags and `If-Match`.
- Canonical JSON response envelopes shared by all transports.
- Authenticated, bounded local transports with fail-closed validation.
- A private controller HostRoute with reserved-route protection.
- An ephemeral zero-listener bootstrap mode for temporary HostRoute provisioning.
- A documented Unix-domain socket security policy, including trusted parent directories and mode `0600`.

The public surface intentionally contains no legacy aggregate configuration facade, settings endpoint, owner-scoped route or service API, collection replacement endpoint, or service lifecycle alias.

## Management API

The complete public resource matrix is:

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/v1` | Read API and capability information. |
| `GET`, `PATCH` | `/v1/global-settings` | Read or update global settings. |
| `GET`, `POST` | `/v1/routes` | List or create routes. |
| `GET`, `PATCH`, `DELETE` | `/v1/routes/{id}` | Read, update, or delete a route. |
| `GET`, `POST` | `/v1/services` | List or create services. |
| `GET`, `PATCH`, `DELETE` | `/v1/services/{id}` | Read, update, or delete a service. |
| `GET`, `PUT`, `DELETE` | `/v1/services/{serviceId}/environment` | Read, replace, or delete a service environment. |
| `GET` | `/v1/extensions` | List installed extension records. |
| `GET` | `/v1/extensions/{extensionId}` | Read an extension record. |
| `GET`, `PUT`, `DELETE` | `/v1/extensions/{extensionId}/settings` | Read, replace, or delete extension settings. |

Resource records expose server-owned identifiers, timestamps, and versions as read-only data. Write operations reject unknown fields and server-owned fields. Service environments are kept out of ordinary service responses and are available only through the dedicated environment resource.

### Concurrency

Every mutation requires exactly one strong, quoted aggregate `If-Match` value obtained from a previous successful response:

- Missing `If-Match`: `428 precondition_required`.
- Malformed or duplicated `If-Match`: `400 invalid_request`.
- Stale or failed compare-and-swap: `412 precondition_failed`.

The server performs one compare-and-swap attempt and does not retry a stale mutation on behalf of the client. Successful responses include the current aggregate ETag; `DELETE` responses use status `204` and carry the updated ETag in the response headers.

## Local Transports

### HostRoute

The HostRoute adapter is registered through the host's route-registration API. The host determines how the route is exposed and how its mount path is reached. The controller does not create a public TCP listener for this transport.

HostRoute requests are mounted under the configured `HostRoutePath` and are normalized to the logical `/v1/...` resource paths before dispatch. HostRoute infrastructure is private to the controller and cannot be created, modified, or deleted through the public route resources.

### HTTP/JSON

When enabled, the self-hosted HTTP adapter binds only to IPv4 and IPv6 loopback:

```text
http://127.0.0.1:<HttpPort>/v1/...
http://[::1]:<HttpPort>/v1/...
```

The transport uses local HTTP/1.1 cleartext. It does not bind remote interfaces and does not provide HTTPS termination.

### gRPC

The gRPC adapter exposes one generic unary gateway:

```text
nekostick.controller.management.v1.ControllerManagement/Invoke
```

It uses HTTP/2 cleartext on IPv4 and IPv6 loopback. The gateway carries the REST method, logical path, headers, and JSON body in an `InvokeRequest`; it does not define an independent resource RPC API.

The application key must be supplied in gRPC metadata using `x-nekostick-controller-key`. It must not be placed in the protobuf request envelope. Application responses preserve the dispatcher status code, headers, and canonical JSON body inside `InvokeResponse`.

### Unix-domain socket

The Unix transport carries HTTP/1.1 over an absolute Unix-domain socket path and is unavailable on Windows. The socket must be created with mode `0600`. Existing socket files are never overwritten. Parent directories must be real directories with no symlink or reparse-point traversal and must not be writable by group or other users. `/tmp` and `/private/tmp`, including paths that resolve through them, are rejected.

The adapter removes a socket only when it can prove that it created the expected socket and that the path and parent chain remain safe. If ownership or safety cannot be proven, cleanup fails closed and leaves the path in place.

## Authentication and Admission

All enabled transports require the same API key. HTTP, Unix, and HostRoute requests provide it through the case-insensitive header:

```text
x-nekostick-controller-key: <API_KEY>
```

The key must be present exactly once, contain no whitespace, be between 32 and 4096 UTF-16 characters, and fit within the implementation's UTF-8 limit. Comparisons use a fixed-time equality check. Missing, duplicate, malformed, weak, oversized, or mismatched keys are rejected without revealing which validation failed.

The controller does not write normal configured keys to responses, status messages, exceptions, or logs. Request and response sizes, method/path lengths, header counts, JSON depth, and embedded JSON documents are bounded; the detailed limits are specified in [`docs/controller-api.md`](docs/controller-api.md).

## Configuration

Controller options are supplied through the host-owned extension settings document. Common options include:

- `loopbackOnly` — must remain `true`.
- `enableHostRoute` and `hostRoutePath` — enable and locate the HostRoute adapter.
- `enableHttpJson` and `httpPort` — enable the loopback HTTP adapter.
- `enableGrpc` and `grpcPort` — enable the loopback gRPC adapter.
- `enableUnixSocket`, `unixSocketPath`, and `unixSocketMode` — enable the Unix transport; the required mode is `0600`.
- `apiKey` — the API key shared by all enabled transports.
- `apiScope` — the supported public capability is `FullConfiguration`.

Options are validated before listeners are started. Invalid, non-local, incomplete, weak, or unsupported configuration fails closed without echoing secret or configuration values. See [`docs/controller-api.md`](docs/controller-api.md) for the complete protocol and validation rules.

## Ephemeral Bootstrap

If all four listener flags are disabled after option hydration, the controller derives a temporary in-memory HostRoute-only configuration for the current entrypoint instance. It generates a random route suffix, a high-entropy API key, and a private ownership marker. The marker and recorded route identity are used to limit cleanup to routes that the current runtime can prove it owns.

The generated bootstrap credential is intentionally not persisted. The current Host Contracts logger ABI does not provide a safe secret-capable delivery channel, so the credential is not emitted to an operator or client. Consequently, zero-listener bootstrap remains a protected but unusable temporary route until an approved delivery mechanism is available.

## Building

The solution can be built with the .NET 10 SDK:

```bash
dotnet build Nekolla.Nekostick.Controller.slnx --nologo -v:q
```

The project treats warnings as errors and generates XML documentation for its public API.

## Repository Layout

```text
.
├── Nekolla.Nekostick.Controller.slnx
├── docs/
│   └── controller-api.md
└── src/
    └── Nekolla.Nekostick.Controller/
        ├── Adapters/
        ├── ControllerEntrypoint.cs
        ├── ControllerOptions.cs
        ├── ManagementApiModels.cs
        ├── ManagementCore.cs
        ├── ManagementDispatcher.cs
        ├── Protos/
        ├── manifest.json
        └── Nekolla.Nekostick.Controller.csproj
```

## Further Documentation

For the complete resource contract, request and response rules, transport behavior, security restrictions, concurrency requirements, and bootstrap lifecycle, see [`docs/controller-api.md`](docs/controller-api.md).
