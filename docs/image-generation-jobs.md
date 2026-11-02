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
the media bytes. Expired leases with a download checkpoint can resume saving.
Without a checkpoint, recovery marks the task `failed` in the `generating`
stage and reports an unknown upstream result; it never regenerates automatically.
There is no separate `review` status.

Downloads carry a provider key only when the final URL is same-origin with the
configured provider base URL. Redirected external CDN URLs are fetched without
the key and are size-limited.

## HTTP contract

The authenticated routes are:

- POST /api/v1/ai/image-jobs
- GET /api/v1/ai/image-jobs/{jobId}
- GET /api/v1/ai/image-jobs/by-submission/{key}
- GET /api/v1/ai/image-jobs?canvasId={canvasId}
- POST /api/v1/ai/image-jobs/{jobId}/retry-download
- POST /api/v1/ai/image-jobs/{jobId}/regenerate

External workflow API keys use the workflow publication contract instead of
these account-session routes.

Submission uses `{"key":"<UUID>","input":{...}}`; the key is a UUID in the body,
not the external workflow's `Idempotency-Key` header. The response exposes
`failureStage` and `canRetryDownload` so the client can present the correct
recovery action. Regeneration takes a new key and reuses the original input
snapshot; editing today's canvas does not change that snapshot.


## Seedream / Ark reference images

Use the `seedream-images` model override for native Ark Seedream models. Keep the
configured base URL (including `/api/plan/v3` when using that service); a full
`/images/generations` suffix is normalized once. Text generation and reference
image generation both use JSON `POST /images/generations`. Authorized reference
assets are read by the server and included as `image` data URLs, never as private
CreativeFlow URLs or multipart `/images/edits` files.

Migration `0048` enables the protocol and assigns it only to identifiable native
Ark `doubao-seedream-*` image models with no explicit protocol override. Relay
hosts, explicit overrides, secrets, base URLs, published snapshots and existing
jobs are retained. Aliased endpoint IDs and relay models can select the protocol
manually in the model settings. Existing failed jobs are not automatically rerun.

Pro/Flash accept PNG/JPEG output. Their quality and random seed controls are hidden;
legacy values are not forwarded. Native controls expose the selected model's
resolution tiers (Pro/Flash: 1K, 1.5K, 2K; Lite: 2K, 3K, 4K; 4.5: 2K, 4K;
4.0: 1K, 2K, 4K). The default remains 2K. Resolution and aspect jointly determine
the actual pixel `size`, retaining the aspect and model pixel limits. Unknown
endpoint aliases expose the common 2K tier only. Explicit canvas sizes retain
precedence until the user changes the native resolution or aspect.

`promptOptimizeMode` is a Seedream-only image input (`standard` / `fast`) and maps
to `optimize_prompt_options.mode`. Fast mode is offered only for Pro and 4.0.
Drafts, queued job snapshots, explicit regeneration and published media workflows
retain the options. Media API publications can expose `resolution` and
`promptOptimizeMode` as enum inputs. Model-specific validation also runs on the
server before generation; unsupported tiers/modes are rejected. OpenAI's
quality, formats, count, size and edit request behavior remain unchanged, and its
adapter does not forward the native optimization field.

Multiple requested pictures use sequential single-image
requests, stop on failure and retain completed outputs; this is not an automatic
retry. Empty prompts, missing authorized reference assets, masks and unsupported
formats fail before a generation request. HTTP failures include the upstream
status and route even when the upstream returns an empty response body.

The original OpenAI image protocol continues to use multipart `/images/edits`
for reference images. Generation is never retried by switching protocols.
