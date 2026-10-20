# Local development

## Isolated Compose stack

Follow [the quick start](../README.md#quick-start-isolated-local-api) to fill
`.env` and start the API with its own dependencies. Keep production resources
and credentials out of this stack.

If a host port is already occupied, set `API_PORT`, `POSTGRES_PORT`,
`REDIS_PORT`, `RABBITMQ_PORT`, and/or `RABBITMQ_MANAGEMENT_PORT` in `.env`.
Container-to-container addresses remain unchanged. Match source-API JSON,
frontend settings and health-check URLs to any changed **host** ports.
For another simultaneous installation, also use a distinct
`COMPOSE_PROJECT_NAME` and `API_IMAGE`; this isolates container/volume names
and image tags. It does not isolate two stacks that use the same host port.

### Podman alternative on Linux

If the host already uses Podman, use Podman 5 with a Compose provider installed
(`docker-compose` v2). Verify `podman compose version` first. Prepare `.env`
as above. Build the image with an explicit architecture, then start the same
Compose file without asking the provider to build it:

~~~bash
# Use linux-musl-x64 via amd64 on x86_64; use arm64 on aarch64.
podman build --build-arg TARGETARCH=amd64 \
  -f src/CreativeFlow.Api/Dockerfile -t creativeflow/api:dev .
podman compose up -d --no-build
podman compose ps
~~~

If `.env` specifies a different `API_IMAGE`, use that same image tag in the
build command. Explicit `TARGETARCH` avoids relying on Docker BuildKit's
automatic build arguments in Podman. Use `podman compose` instead of
`docker compose` for subsequent lifecycle commands, keep the same project
name, and do not use `down -v` for a restart. The optional OpenSandbox profile
requires a Docker-compatible daemon socket; the default Docker socket mount
is **not** automatically compatible with a Podman host.

If the Compose provider is installed outside the normal executable path, set
`PODMAN_COMPOSE_PROVIDER` to its absolute executable path before running these
commands. Do not overwrite another application's provider installation.

If the image build stalls downloading Alpine packages, choose a trusted HTTPS
endpoint from the [official Alpine mirror list](https://mirrors.alpinelinux.org/).
Compose reads `ALPINE_MIRROR` from `.env`; for a direct Podman build also pass
`--build-arg ALPINE_MIRROR=https://your-trusted-mirror/alpine`. This changes only
the package download location, not the Alpine release or APK signature checks.
Image-registry connectivity is separate: review your runtime's registry/proxy
configuration instead of disabling TLS or reusing an unrelated image tag.

## Run the API from source

Install the SDK pinned in `global.json`. Prepare `.env` as described in the
README, then start **only** the infrastructure (stop the Compose API first
if it is already running):

~~~bash
docker compose stop api
docker compose up -d postgres redis rabbitmq
cp deploy/development/appsettings.Development.local.example.json \
  src/CreativeFlow.Api/appsettings.Development.local.json
~~~

Edit the copied JSON. Match the PostgreSQL and RabbitMQ passwords to `.env`,
set an initial admin password and device pepper, and set `Ai:EncryptionKey`
to `openssl rand -base64 32`. The template uses PostgreSQL port 5432,
Redis 6380, RabbitMQ 5673, and disables knowledge indexing until Qdrant is ready.
The API does **not** read the root `.env`; Compose and dotnet have different
configuration loaders.

~~~bash
dotnet restore CreativeFlow.slnx --locked-mode
dotnet build CreativeFlow.slnx
dotnet run --project src/CreativeFlow.Api -- --migrate-only
dotnet run --project src/CreativeFlow.Api
~~~

These commands use the Development launch profile and port 5298. The migrator
exits after applying migrations; only the last command starts the server.
Local files are stored under `src/CreativeFlow.Api/storage` when launched this
way and are ignored by Git. Do not point the template at a shared or production
database. Private JSON overrides are not copied by `dotnet publish`.

Open [Swagger](http://localhost:5298/swagger/index.html) and verify:

~~~bash
curl -fsS http://localhost:5298/health/live
curl -fsS http://localhost:5298/health/ready
~~~

Swagger is currently exposed in all environments. Restrict it at a reverse
proxy where API documentation should not be public.

## Frontend and end-to-end startup

The frontend is a separate Next.js repository:
[creativeflow-frontend](https://github.com/233i/creativeflow-frontend).
A backend clone does not contain the UI. Use a matching frontend/backend
revision and the independent local stack above. The current Hub is
`/hubs/creativeflow`; the API source project is `src/CreativeFlow.Api`.

### Start the web app

Install the Node version pinned by the frontend's `.node-version`, then run
these commands in a **separate terminal**, beside your backend clone:

~~~bash
git clone https://github.com/233i/creativeflow-frontend.git
cd creativeflow-frontend
npm ci
cp .env.example .env.development.local
~~~

In the copied frontend file, set these non-secret addresses:

~~~dotenv
API_BASE_URL=http://localhost:5298
SOL_API_ORIGIN=http://localhost:5298
NEXT_PUBLIC_SITE_URL=http://localhost:3000
~~~

`SOL_API_ORIGIN` is the frontend's current configuration key; do not rename
it when using the CreativeFlow backend. Existing shell environment variables
take priority; `.env.development.local` takes priority over `.env.local` during
development. Preserve existing files, but check all of these sources if requests
unexpectedly go to another server.

Set the backend's browser-facing origin to `http://localhost:3000`:

- Compose API: change `PUBLIC_ORIGIN` in the backend `.env`, then run
  `docker compose up -d api` to recreate that service with the new value.
- Source API: add `"Authentication": { "PublicOrigin": "http://localhost:3000" }`
  to the private Development JSON and restart the API.

Start the frontend:

~~~bash
npm run dev
curl -fsS http://localhost:3000/api/v1/system/info
~~~

The response must identify `creativeflow-api` in `Development`. The browser
should use `http://localhost:3000` consistently; do not mix `localhost` and
`127.0.0.1` when signing in because cookies are host-specific. The frontend
proxies `/api` and `/hubs` to the backend; no CORS wildcard is needed. Its
rewrites do not proxy `/health` or `/swagger`, so test those on port 5298.
Next.js rewrites do not guarantee WebSocket upgrade support; verify SignalR
fallback transport locally, and use a WebSocket-capable reverse proxy for a
production deployment.

### Accept the complete local installation

1. Open `http://localhost:3000/admin`. Sign in with the backend's bootstrap
   administrator and change the initial password. This is **not** the ordinary
   user login: administrator and canvas-user accounts are separate.
2. Open the workflow center and import
   [startup-echo.workflow.json](examples/startup-echo.workflow.json).
   Run it with `message` set to `Hello CreativeFlow`. Both **Start** and **End**
   must succeed, and the final `result` must equal `Hello CreativeFlow`.
   This checks queue/worker execution and persisted results without a paid model.
3. Open `/`, register a disposable ordinary user, and create a canvas. Upload
   a small test image, select it from the asset library, then refresh the page.
   The canvas and image must still be available; uploading does not call a model.
4. Restart the API without deleting volumes (Compose: `docker compose restart api`;
   source: stop and start only your API process). Repeat the readiness check,
   reload the workflow history and canvas, and download the image. Existing
   results/files must be retained. Restarting must not reset the admin password.
5. Only after those checks, configure a model provider, enable the desired
   model/slots, and allow the precise workflow model endpoint. Make any paid
   generation test explicitly; it is not part of the no-cost startup test.

Optional knowledge search also needs Qdrant, an embedding endpoint and measured
dimensions; scripts need OpenSandbox. A healthy base API does not certify these
services. Follow [knowledge setup](knowledge.md) and the sandbox section below
before testing those features.

For production-mode frontend verification, follow the
[frontend deployment guide](https://github.com/233i/creativeflow-frontend/blob/main/docs/deployment.md).
Keep the backend in Development for HTTP-only local testing; Production API
cookies require HTTPS. Frontend production mode is not the same as changing the
backend environment to Production.

## Tests

~~~bash
dotnet test CreativeFlow.slnx --no-restore
git diff --check
~~~

Without `CREATIVEFLOW_TEST_POSTGRES`, database-dependent tests are skipped;
this is **not** a full persistence acceptance run. For complete checks, create
a separate empty, disposable PostgreSQL 18 database, set its connection string,
and run:

~~~bash
export CREATIVEFLOW_TEST_POSTGRES='Host=127.0.0.1;Port=5432;Database=creativeflow_tests;Username=creativeflow;Password=REPLACE_WITH_TEST_PASSWORD'
bash scripts/verify-maf-migration.sh
~~~

Never point tests at an application database. Tests create and remove their own
schemas. **Known test limitation:** schema-isolated database fixtures do not
currently match migration 0045, which operates on `public`. The database suite
can therefore fail with missing `creativeflow_*` relations even when the real
empty-database migration succeeds. Adapt the fixture isolation strategy before
using this suite as a release gate; do not edit historical migrations or disable
the failing cases. The strict verifier also checks XML comments and may report
existing documentation gaps.

The locked MessagePack dependency also has a reported security advisory
(`NU1902` during restore/build). Treat this as a dependency maintenance item,
not a startup configuration fix. A successful isolated startup does not mean
the complete test or security gate is green.

The [isolated integration harness](../tests/maf-e2e/README.md) provides
loopback model fixtures, restart checks, and HTTP/SignalR contract checks
without using paid providers.

## Optional OpenSandbox

The default stack neither starts OpenSandbox nor mounts the Docker socket.
To opt in:

~~~bash
cp opensandbox/config.toml opensandbox/config.local.toml
~~~

Replace the API key placeholder in `config.local.toml` with a new key and set
the **same** value in `.env` as `OPENSANDBOX_API_KEY`. Set
`SKILLS_SANDBOX_ENABLED=true`, then start:

~~~bash
docker compose --profile sandbox up -d --build
~~~

Review the image versions, resource limits, and Docker access in
`opensandbox/config.toml` before enabling this on any trusted host.
`config.local.toml` is excluded from Git and Docker build context.
Production should pin reviewed image tags/digests rather than `latest`.

To turn it off, set `SKILLS_SANDBOX_ENABLED=false`, recreate the API with
`docker compose up -d api`, and stop the control plane using
`docker compose --profile sandbox stop opensandbox`.

## Troubleshooting

- Readiness fails: inspect `docker compose logs api postgres redis rabbitmq`
  and check all four dependencies, including writable file storage.
- A port is occupied: stop only the instance you started or choose another
  host port and update the source-development JSON to match.
- Wrong password after editing `.env`: existing database volumes keep their
  original credentials; perform an explicit password rotation instead.
- Invalid encryption key: use 32 random bytes encoded in Base64, not a
  64-character hex string.
- Migrations fail: inspect the error and the selected database; do not edit
  historical SQL or checksums and do not erase volumes as a repair shortcut.
- SSE is buffered: disable reverse-proxy buffering and test the API directly.
- Endpoint denied: configure the exact model, embedding, rerank, or HTTP
  endpoint in the relevant allowlist; preserve the loopback Qdrant policy.
