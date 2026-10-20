# CreativeFlow Backend

The .NET 10 backend for CreativeFlow: an AI canvas, media workflows, general
orchestration, knowledge retrieval, assets, and Agent tools.

[中文](README.zh-CN.md) · [Documentation](docs/README.md) ·
[Frontend repository](https://github.com/233i/creativeflow-frontend)

## What you can run

- Media workflows and typed orchestration with durable run/node state.
- Provider/model management, image/video generation, and an asset library.
- Knowledge ingestion, semantic chunks, vector/keyword/hybrid retrieval and optional reranking.
- HTTP nodes, structured outputs, parameter extraction and OpenSandbox scripts.
- Published workflow APIs: REST polling, custom SSE, or AG-UI events.

**This repository does not include the browser UI.** The API provides Swagger;
the separately deployed frontend provides the canvas and administration pages.
The base stack needs no model key. Generation, knowledge and sandbox execution
require separately configured providers/services and may incur costs.

## Quick start: isolated local API

Requirements: Git and Docker with Compose v2. Docker builds the API, so a host
.NET SDK is not needed for this path. Run in a new checkout:

```bash
git clone https://github.com/233i/creativeflow-backend.git
cd creativeflow-backend
cp .env.example .env
openssl rand -hex 32
openssl rand -base64 32
```

Edit the ignored `.env` before starting. Generate separate values for:

| Variable | Required value |
| --- | --- |
| `POSTGRES_PASSWORD` | Random database password |
| `RABBITMQ_PASSWORD` | Different random broker password |
| `ADMIN_PASSWORD` | Strong bootstrap administrator password |
| `DEVICE_PEPPER` | Independent random value, e.g. `openssl rand -hex 32` |
| `AI_ENCRYPTION_KEY` | `openssl rand -base64 32` — exactly 32 decoded bytes, not hex |
| `PUBLIC_ORIGIN` | `http://localhost:3000` for the local frontend below |

Keep the encryption key stable; it protects saved credentials.

```bash
docker compose up -d --build
docker compose ps
curl --retry 20 --retry-delay 3 --retry-all-errors --max-time 10 \
  -fsS http://localhost:5298/health/ready
curl -fsS http://localhost:5298/health/live
```

Open [Swagger](http://localhost:5298/swagger/index.html).
Readiness checks PostgreSQL, Redis, RabbitMQ and file storage—not optional models.
The initial build may take several minutes.

The stack starts PostgreSQL 18, Redis 8, RabbitMQ 4, a storage initializer and
the API. Ports bind to **127.0.0.1**. Database and files use persistent volumes;
this is a local stack, not a production manifest. For occupied ports or Podman,
see [local development](docs/local-development.md#isolated-compose-stack).

### Run the complete application

In another terminal:

```bash
git clone https://github.com/233i/creativeflow-frontend.git
cd creativeflow-frontend
# Install the Node version in .node-version first.
npm ci
cp .env.example .env.development.local
npm run dev
```

Open [the admin UI](http://localhost:3000/admin), sign in as `ADMIN_USERNAME`
(default `admin`) with the password above, then change the initial password.
Import [startup-echo.workflow.json](docs/examples/startup-echo.workflow.json)
in the workflow center: `message = Hello CreativeFlow` must produce
`result = Hello CreativeFlow` without any model call.

For the user canvas at `/`, register a separate ordinary account.
Configure models in the admin UI before generating media. Complete acceptance,
including upload, asset reuse and persistence after restart, is described in
[frontend integration](docs/local-development.md#frontend-and-end-to-end-startup).

### Stop without deleting data

```bash
docker compose down
docker compose up -d
```

Do not use `down -v` unless intentionally erasing the local instance.
Changing bootstrap passwords in `.env` does not reset existing database,
broker or administrator credentials.

## Choose the next guide

| Task | Guide |
| --- | --- |
| Run/edit the API from source | [Local development](docs/local-development.md) — SDK pinned in `global.json` |
| Deploy a server | [Configuration](docs/configuration.md) → [Deployment](docs/deployment.md) → [Operations](docs/operations.md) |
| Configure knowledge | [Knowledge](docs/knowledge.md) — Qdrant loopback policy, embeddings and quotas |
| Enable scripts | [OpenSandbox setup](docs/local-development.md#optional-opensandbox) |
| Integrate another system | [REST/SSE](docs/workflow-api-streaming.md) or [AG-UI](docs/ag-ui-workflow-api.md) |
| Maintain the backend | [Architecture](docs/architecture.md), [data/migrations](docs/data-model.md), [coding standards](docs/coding-standards.md) |

The [documentation index](docs/README.md) covers the remaining feature contracts.
Preserve migrations **0001–0045 and their checksum file unchanged**; Git history
cleanup is not a database reset. Read the migration guide before upgrading an
installation that still stores media in database blobs.

## Verification and security

This is an actively developed source release. Base startup and full browser
integration can work while quality gates still have known failures. See
[tests and limitations](docs/local-development.md#tests) before accepting a release;
optional provider-backed features need their own acceptance checks.

Do not commit secrets, uploads, local configuration or build artifacts.
Use HTTPS for Production, review outbound allowlists and resource grants, and
restrict Swagger at the reverse proxy if it should not be public.

## License

CreativeFlow is derived from [AIDotNet/Sol](https://github.com/AIDotNet/Sol).
The upstream MIT license and `Copyright (c) 2026 AIDotNet` notice are preserved
in [LICENSE](LICENSE). This attribution does not imply upstream maintenance
or endorsement of CreativeFlow.
