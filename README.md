# CreativeFlow Backend

CreativeFlow is a .NET 10 API for AI canvas editing, media generation, durable
workflows, knowledge retrieval, and Agent tools. It includes external workflow
APIs and AG-UI-compatible SSE events.

[中文快速部署](README.zh-CN.md) · [Documentation](docs/README.md)

## What is included?

- Media workflows and general-purpose orchestration, with node status, run
  history, publication, and access controls.
- Provider/model management, image/video generation, and an asset library.
- Knowledge ingestion, semantic chunks, hybrid retrieval, reranking, and citations.
- HTTP tools, structured output, parameter extraction, and sandboxed scripts.
- PostgreSQL metadata, a persistent file store, Redis, RabbitMQ, and SignalR.

This repository is the **backend**, not the browser UI. A successful start
provides Swagger and API endpoints; deploy the frontend separately for the
canvas and administration pages. Models, embedding services, Qdrant, and
OpenSandbox require their own configuration.

For the complete browser application, finish the backend steps below, then
follow [frontend setup and acceptance](docs/local-development.md#frontend-and-end-to-end-startup).
Backend startup alone does not provide a web page at `/`.

## Quick start: isolated local API

Requirements: Git and Docker with the Compose v2 plugin. Docker Desktop works
on macOS/Windows; use Docker Engine on Linux. Run these commands at the root
of your clone.

Already using Podman on Linux? Follow the
[explicit Podman build/start variant](docs/local-development.md#podman-alternative-on-linux).

### 1. Set local secrets

~~~bash
cp .env.example .env
openssl rand -hex 32
openssl rand -base64 32
~~~

Edit the ignored `.env` file before starting:

| Variable | Value to supply |
| --- | --- |
| `POSTGRES_PASSWORD` | A new random password |
| `RABBITMQ_PASSWORD` | A different random password |
| `ADMIN_PASSWORD` | A strong initial admin password |
| `DEVICE_PEPPER` | A new random value, e.g. the output of `openssl rand -hex 32` |
| `AI_ENCRYPTION_KEY` | The output of `openssl rand -base64 32` — exactly 32 decoded bytes, **not hex** |

Generate separate values; never reuse production secrets. Keep the encryption
key stable across restarts. Leave optional OAuth and sandbox settings disabled.

### 2. Start and check

~~~bash
docker compose up -d --build
docker compose ps
curl --retry 20 --retry-delay 3 --retry-all-errors --max-time 10 \
  -fsS http://localhost:5298/health/ready
curl -fsS http://localhost:5298/health/live
~~~

The first build can take several minutes. Open
[Swagger UI](http://localhost:5298/swagger/index.html) in your browser. Readiness
must report PostgreSQL, Redis, RabbitMQ, and file storage as healthy.

The default stack starts only the API, PostgreSQL 18, Redis 8, RabbitMQ 4, and
a one-shot storage initializer. It applies migrations to its own database.
Ports bind to **127.0.0.1 only**; this is not a production manifest.
If default ports are occupied, use the optional port variables in `.env`;
see [isolated stack configuration](docs/local-development.md#isolated-compose-stack).

### 3. Sign in and configure models

Use `ADMIN_USERNAME` (default `admin`) and your `ADMIN_PASSWORD` in the separately
deployed frontend, or Swagger's `POST /api/v1/admin/auth/login`. Change the
bootstrap password when prompted. Bootstrap settings do not reset existing
administrator accounts.

Configure a provider URL, key, and models before making model calls; no
provider credentials are included. For workflow/embedding/HTTP nodes, also
configure the corresponding outbound allowlists. See
[configuration](docs/configuration.md) and [authentication](docs/authentication.md).

Before paying for any model calls, import the
[startup echo workflow](docs/examples/startup-echo.workflow.json) in the frontend
workflow center. It returns the supplied text without a model, knowledge
library, or external HTTP request. The local development guide includes the
expected result and the restart/persistence checks.

### Stop, restart, and data

~~~bash
docker compose down
docker compose up -d
~~~

Named volumes retain database and media data. **Do not add `-v` to `down`
unless you intend to erase this local instance.** Changing database passwords
in `.env` does not rotate passwords inside an already initialized volume.

## Source development

Install the SDK pinned in [global.json](global.json) and use the
[local development guide](docs/local-development.md). It covers infrastructure,
private settings, explicit migrations, and isolated database tests:

~~~bash
docker compose up -d postgres redis rabbitmq
cp deploy/development/appsettings.Development.local.example.json \
  src/CreativeFlow.Api/appsettings.Development.local.json
# Fill the copied settings first; dotnet does not read .env automatically.
dotnet restore CreativeFlow.slnx --locked-mode
dotnet build CreativeFlow.slnx
dotnet run --project src/CreativeFlow.Api -- --migrate-only
dotnet run --project src/CreativeFlow.Api
~~~

Do not run these migration commands against a shared or production database.

## Optional services

- **Knowledge:** the base Compose stack disables it. The current Qdrant adapter
  accepts only `http://127.0.0.1:<port>` from the API's network namespace;
  production requires port 6333. Ordinary container DNS names or remote
  Qdrant URLs do not pass this policy. See [knowledge setup](docs/knowledge.md).
- **OpenSandbox:** disabled by default and behind the `sandbox` Compose
  profile. It requires a private local config and Docker daemon access.
  See [local development](docs/local-development.md#optional-opensandbox).
- **Frontend:** serve the UI and proxy `/api`, `/hubs`, and streaming endpoints
  through the same origin. Swagger is not the administration UI.

## Production deployment

Follow the [deployment guide](docs/deployment.md): build an immutable image,
inject secrets using [the production template](deploy/production/backend.env.example),
prepare persistent volumes, run `--migrate-only` once, then start the API behind
HTTPS. The guide includes Linux Docker commands and health checks.

Keep databases and sandbox services private. Back up PostgreSQL **and** the
asset/Skill volumes. Preserve all SQL migrations and their checksum contract;
do not replace an existing database with a fresh schema.

## Reference

- [Architecture](docs/architecture.md) and [data model](docs/data-model.md)
- [AI/canvas API](docs/ai-canvas-api.md) and [asset center](docs/asset-center.md)
- [Workflow model](docs/workflows.md), [AG-UI API](docs/ag-ui-workflow-api.md),
  and [streaming contract](docs/workflow-api-streaming.md)
- [HTTP nodes and credentials](docs/workflow-http-request-node.md)
- [Operations and recovery](docs/operations.md)
- [Complete documentation index](docs/README.md)

## Verification status

This is a source release under active development, not a claim that every quality
gate passes. Isolated startup, fresh database migrations, login, no-model workflow
execution, asset persistence, and frontend integration have been exercised.
Known remaining items include database-test schema isolation around migration
0045 and a MessagePack dependency security advisory. Do not disable tests or
audit checks to hide these issues; see [local verification](docs/local-development.md).
Provider-backed generation, knowledge services, and sandbox execution require
separate configuration and acceptance in your environment.

## Security and license

Do not commit keys, passwords, local configuration, uploaded files, or build
artifacts. Test fixtures use synthetic credentials only. The API exposes
Swagger in production too: restrict it at your reverse proxy if necessary.
Review outbound allowlists and resource permissions before exposing an instance.

CreativeFlow is derived from [AIDotNet/Sol](https://github.com/AIDotNet/Sol).
The upstream code is licensed under the MIT License. Its original copyright
notice, `Copyright (c) 2026 AIDotNet`, and full license text are preserved in
[LICENSE](LICENSE). This attribution identifies the upstream project; it does
not imply that AIDotNet maintains or endorses CreativeFlow.
