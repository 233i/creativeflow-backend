# Operations

## Health and observability

- /health/live reports process liveness.
- /health/ready verifies required dependencies.
- Multi-instance realtime deployments must set Realtime:Backplane=Redis and
  verify cross-instance delivery; the default None is for a single instance.
- Application logs must include a request/run identifier but must not include
  provider keys, passwords, full authorization headers, or media bytes.
- Track workflow queue depth, node duration, provider failures, SSE disconnects,
  database pool saturation, and file-volume capacity.

## Storage and retention

The file store contains uploaded assets, generated media, previews, and Skill
packages. PostgreSQL contains metadata, checksums, workflow state, and audit
records. Back up both stores together and test a restore regularly. Use a
retention policy for failed jobs and orphaned files; do not remove an asset
while a workflow, published API, or audit record still references it.

## Secrets

Keep all credentials in a secret manager. Restrict access to administrator
sessions and deployment identities. Rotate provider keys independently from
Ai:EncryptionKey; rotate the encryption key only with a re-encryption plan.
After a bootstrap admin login, replace the bootstrap password.

## Database changes

Migration files in db/migrations are append-only release inputs. Back up the
database before applying them, apply each migration once, and record the
resulting schema version. For a multi-instance deployment, run migrations from
a dedicated release job and keep Postgres:RunMigrationsOnStartup=false.

## Outbound access

Model, embedding, HTTP-node, Qdrant, and OpenSandbox requests are restricted by
configuration. Use private networking where possible and maintain a narrow
allowlist of required endpoints. Review new endpoints as a security change.

## Recovery

Keep the previous container image, the matching database backup, and the
matching file-volume snapshot until a release has passed its verification
window. A binary-only rollback is unsafe when the schema or persisted workflow
snapshot changed.
