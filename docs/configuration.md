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

The settings are separate: `Workflow:AllowedEndpoints` for model calls,
`Workflow:HttpRequests:AllowedEndpoints` for HTTP nodes,
`Embedding:AllowedEndpoints` for embedding requests, and
`Rerank:AllowedEndpoints` for rerank requests. A model in the catalog does not
automatically grant outbound access.

Qdrant uses a stricter built-in policy, not these URL allowlists: literal
`127.0.0.1` over HTTP, no extra path, matching `cfdev_`/`cfprod_` namespace,
and port 6333 in Production. See [knowledge setup](knowledge.md).
