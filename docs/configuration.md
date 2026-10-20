# Configuration

CreativeFlow uses the standard ASP.NET Core configuration chain:

1. committed appsettings.json and environment-specific settings;
2. ignored local override files such as appsettings.Development.local.json;
3. .NET User Secrets in Development (when configured);
4. environment variables;
5. command-line arguments.

Later sources win. The application does not read .env files by itself;
Docker Compose expands .env and passes the resulting values as environment
variables.

## Required production values

| Key | Purpose |
| --- | --- |
| Postgres:ConnectionString | Primary database |
| Postgres:RunMigrationsOnStartup | Set false for multi-instance release deployments |
| Redis:Configuration | Cache, locks, and optional SignalR backplane |
| RabbitMq:* | Durable integration events |
| Authentication:PublicOrigin | Canonical HTTPS browser/OAuth origin |
| Admin:DefaultUsername / Admin:DefaultPassword | Bootstrap administrator |
| DeviceIdentity:Pepper | Server-only device identity pepper |
| Ai:EncryptionKey | Key used to encrypt provider credentials |
| Ai:AssetRoot | Persistent media directory |
| Skills:Root | Persistent Skill directory |
| Skills:SandboxEnabled | OpenSandbox script execution switch |
| Knowledge:Qdrant:* | Qdrant endpoint, namespace, and API key |
| Workflow:AllowedEndpoints:* | Outbound workflow/model allowlist |
| Embedding:AllowedEndpoints:* | Outbound embedding allowlist |

The source templates are
[Development](../deploy/development/appsettings.Development.local.example.json) and
[Production](../deploy/production/backend.env.example).
Compose additionally uses root [.env.example](../.env.example); its uppercase variable
names are mapped in [docker-compose.yml](../docker-compose.yml). Do not use Compose
variable names such as `POSTGRES_PASSWORD` as a substitute for .NET configuration keys.
With .NET environment variables, replace `:` with `__`, for example `Ai__AssetRoot`.

## Feature switches

| Setting | Effect |
| --- | --- |
| `Workflow:Enabled` | General orchestration model/runtime capabilities; set in Development/Production JSON |
| `Workflow:HttpRequests:Enabled` | HTTP node capability; also requires Workflow enabled |
| `Knowledge:Enabled` | Knowledge library/index/retrieval capability |
| `Skills:SandboxEnabled` | OpenSandbox execution; defaults to false |
| `ExternalWorkflowApi:Enabled` | External workflow endpoints |

The base Compose stack and production env template explicitly disable knowledge
until Qdrant is configured, overriding the environment JSON. Source Development
uses the copied local JSON to disable it. Check the **effective** configuration,
not just one file. Changing configuration requires recreating/restarting the API.

## Secret handling

Use a secret manager or protected environment file for passwords, provider API
keys, peppers, encryption keys, and Qdrant/OpenSandbox keys. Do not put them in
appsettings JSON, workflow exports, example files, or logs. The admin bootstrap
password should be rotated immediately after the first login.

Ai:EncryptionKey must decode from Base64 to exactly 32 bytes. Generate it with
`openssl rand -base64 32`; a hexadecimal string is not interchangeable.
It must be stable while encrypted provider credentials remain in
the database. Rotating it requires a planned credential re-encryption process.
Rotating DeviceIdentity:Pepper invalidates existing device fingerprints.

## Storage

Ai:AssetRoot and Skills:Root must be writable persistent directories and contain
.creativeflow-storage markers when the corresponding RequireStorageMarker option
is enabled. PostgreSQL stores metadata and checksums, not the media bytes.

## Endpoint allowlists

Workflow model calls, embedding requests, and HTTP request nodes are checked
against endpoint allowlists. Prefer complete HTTPS URLs. Broad wildcard patterns
increase the set of reachable services. A development loopback exception is not a production
configuration. Keep the allowlist in deployment configuration so changing a
provider does not require editing source code.

| Setting | Matching / default |
| --- | --- |
| `Workflow:AllowedEndpoints` | Exact model request URLs; explicitly supports `https://*` for public HTTPS |
| `Embedding:AllowedEndpoints` | Exact URLs; no wildcard |
| `Rerank:AllowedEndpoints` | Exact URLs; no wildcard |
| `Workflow:HttpRequests:AllowedEndpoints` | HTTP-node policy; when absent defaults to `https://*`; explicit empty configuration denies all |

A model in the catalog does not automatically grant outbound access. HTTP
credential grants separately restrict method and endpoint; a network wildcard
does not remove those restrictions. For a locked-down deployment, configure
HTTP allowed endpoints explicitly rather than assuming its absent setting is deny-all.
In the model/embedding/rerank policies, Production requires public HTTPS even
for an exact URL; only Development permits explicitly listed loopback services.
Wildcard model access still rejects private/reserved IPs and does not follow redirects.

Example environment keys (replace the example host and paths):

```dotenv
Workflow__AllowedEndpoints__0=https://provider.example/v1/chat/completions
Embedding__AllowedEndpoints__0=https://provider.example/v1/embeddings
Rerank__AllowedEndpoints__0=https://provider.example/v1/rerank
Workflow__HttpRequests__AllowedEndpoints__0=https://business.example/query
```

These are complete request URLs, **not** provider Base URLs. Gemini streaming
needs its model-specific `:streamGenerateContent?alt=sse` URL as well as the
non-streaming address when using exact matching. See [streaming](workflow-api-streaming.md).
ASP.NET configuration merges indexed arrays: overriding index 0 alone does not
necessarily remove later indexes from earlier sources. Review every effective entry.

Qdrant uses a stricter built-in policy, not these URL allowlists: literal
`127.0.0.1` over HTTP, no extra path, matching `cfdev_`/`cfprod_` namespace,
and port 6333 in Production. See [knowledge setup](knowledge.md).
