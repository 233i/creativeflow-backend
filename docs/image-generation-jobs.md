# Asynchronous image jobs

The image-generation API accepts a request, persists a job, and returns a job
ID before the provider finishes. The browser can close and later query the same
job. This contract describes the canvas image-job queue. Workflows and Agents
use their own run lifecycle; do not assume they submit through these routes.

## Lifecycle

The visible stages are queued, generating, saving, succeeded, and failed.
Clients should render the stage, elapsed time, and the server timestamps rather
than inventing a percentage.

Use these recovery operations deliberately:

| Operation | Model call |
| --- | --- |
| Query job status | No |
| Retry download/save from an upstream checkpoint | No |
| Regenerate from the immutable input snapshot | Yes |

Submission keys are idempotent per account and parameter snapshot. A repeated
submission returns the existing job; a changed request with the same key is
rejected. The server also applies per-account, per-provider, and global
concurrency limits.

## Storage

Provider output is written to the persistent file store and then associated with
an asset record. PostgreSQL stores job state, checksums, and references, not
the media bytes. A restart can resume a known download checkpoint; an unknown
provider result is marked for review and is never regenerated automatically.

Downloads carry a provider key only when the final URL is same-origin with the
configured provider base URL. Redirected external CDN URLs are fetched without
the key and are size-limited.

## HTTP contract

The authenticated routes are:

- POST /api/v1/ai/image-jobs
- GET /api/v1/ai/image-jobs/{jobId}
- GET /api/v1/ai/image-jobs/by-submission/{key}
- POST /api/v1/ai/image-jobs/{jobId}/retry-download
- POST /api/v1/ai/image-jobs/{jobId}/regenerate

External workflow API keys use the workflow publication contract instead of
these account-session routes.
