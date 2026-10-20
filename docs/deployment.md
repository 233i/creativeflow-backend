# Deployment

This guide targets a **Linux host** with Docker and a same-host HTTPS reverse
proxy. The API runs on the .NET JIT runtime. The local Compose file is a demo
stack, not a production deployment manifest.

## 1. Build a release

~~~bash
dotnet restore CreativeFlow.slnx --locked-mode
dotnet build CreativeFlow.slnx -c Release --no-restore
dotnet test CreativeFlow.slnx -c Release --no-build
docker build -f src/CreativeFlow.Api/Dockerfile -t creativeflow/api:release .
~~~

Use an immutable tag or image digest for a real release and retain the previous
image for rollback. Database integration tests need a separate disposable
database; see [local development](local-development.md#tests).

## 2. Prepare dependencies and persistent storage

Provide PostgreSQL 18, Redis, and RabbitMQ on private endpoints. Qdrant is
optional; its current adapter requires loopback access from the API and port
6333 in Production, not an arbitrary remote address. See
[knowledge configuration](knowledge.md#deployment-configuration).

Prepare host directories and grant the API's container user read/write access:

~~~bash
sudo mkdir -p /var/lib/creativeflow/assets /var/lib/creativeflow/skills
sudo touch /var/lib/creativeflow/assets/.creativeflow-storage
sudo touch /var/lib/creativeflow/skills/.creativeflow-storage
~~~

A marker confirms the intended volume is present. Do not create it in an
ephemeral container layer to hide a missing mount. PostgreSQL contains
metadata, not a second copy of the media files.

## 3. Configure secrets

~~~bash
cp deploy/production/backend.env.example deploy/production/backend.env
chmod 600 deploy/production/backend.env
openssl rand -base64 32
openssl rand -hex 32
~~~

Edit `backend.env`: replace **all** example hosts and required placeholders,
including the database/cache/broker credentials, public HTTPS origin, bootstrap
admin password, and device pepper. `Ai__EncryptionKey` must be the Base64
encoding of exactly 32 random bytes. Preserve it for the life of encrypted
credentials. Optional knowledge/sandbox settings remain disabled until ready.

The ignored file is loaded by Docker's `--env-file`, not automatically by
ASP.NET Core. Keep `Postgres__RunMigrationsOnStartup=false`.
Add the exact endpoint allowlists for model, embedding, rerank, and HTTP nodes;
`Workflow__AllowedEndpoints` and `Workflow__HttpRequests__AllowedEndpoints`
are distinct.

The following commands use Linux `--network host` and bind the API to
`127.0.0.1:5298`. The reverse proxy must run on the same host. For a different
container topology, review network access, especially Qdrant's loopback
restriction; do not blindly change its URL to a Docker service name.

## 4. Apply migrations once

For an existing installation, first stop submissions/drain active work and
back up PostgreSQL plus both file volumes. In a controlled release job:

~~~bash
docker run --rm --network host \
  --env-file deploy/production/backend.env \
  -v /var/lib/creativeflow/assets:/var/lib/creativeflow/assets \
  -v /var/lib/creativeflow/skills:/var/lib/creativeflow/skills \
  creativeflow/api:release --migrate-only
~~~

Continue only after exit code 0. This uses the application's migration runner,
including application-level migration logic. Do not substitute a loop of
`psql -f` commands, edit applied SQL, or replace the checksum contract.
The same migration chain supports a fresh empty database; no separate
destructive reset script is required.

## 5. Start the API

~~~bash
docker run -d --name creativeflow-api --restart unless-stopped \
  --network host \
  --env-file deploy/production/backend.env \
  -v /var/lib/creativeflow/assets:/var/lib/creativeflow/assets \
  -v /var/lib/creativeflow/skills:/var/lib/creativeflow/skills \
  creativeflow/api:release
curl -fsS http://127.0.0.1:5298/health/live
curl -fsS http://127.0.0.1:5298/health/ready
~~~

The name must be free; updating an existing instance requires your normal
controlled container replacement process, not starting a second migrator.
Readiness covers PostgreSQL, Redis, RabbitMQ, and file storage; it does not
prove model, Qdrant, or OpenSandbox availability.

The initial admin is created only when absent. Sign in through the frontend
or admin auth API and change the initial password. Changing the bootstrap
environment value later will not reset an existing account.

## 6. HTTPS, frontend, and streaming

Serve the separately built frontend and proxy `/api`, `/hubs`, and relevant
streaming paths to the API on the same origin. Set
`Authentication__PublicOrigin` to that HTTPS origin. Support WebSocket upgrades
for SignalR and disable response buffering on SSE routes. Configure proxy
timeouts and request-body limits deliberately.

Keep PostgreSQL, Redis, RabbitMQ, Qdrant, and OpenSandbox off the public
network. Swagger is enabled in Production too; restrict `/swagger` at the
proxy if it is intended only for operators.

Verify the public health endpoints, administrator login, a non-billable
workflow, asset download/Range behavior, and incremental SSE events.
Model tests may incur charges and should be an explicit operator decision.

## 7. Backups and recovery

Record the image digest, schema version, configuration revision, and backup
location at each release. Back up the database, assets, Skills, and optional
Qdrant data. Securely preserve the encryption key separately.

Old binaries are not automatically compatible with newer schemas. Restore
a coherent database/file snapshot only under a reviewed rollback plan; do
not point an older image at a newer schema and assume compatibility.
