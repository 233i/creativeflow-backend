# Asset center

The asset center manages uploaded images, generated images/videos, previews, and
derived files. The browser asset picker and administrator pages use the same
asset records and authorization rules.

## Storage model

PostgreSQL stores asset metadata, ownership, checksums, references, and audit
events. Media bytes live in the persistent directory configured by Ai:AssetRoot.
The API streams downloads and supports video Range requests; it never loads the
whole media file from a database blob.

Each physical object is content-addressed by SHA-256. Public reuse can share
the physical object while creating a new logical asset for the receiving
account. Storage markers and volume permissions are validated before writes.

## Lifecycle

1. Upload or generate a file and wait for the metadata and file write to finish.
2. Use the asset picker to select private or explicitly published public assets.
3. Move unwanted assets to the recycle bin. This does not immediately delete
   bytes and prevents new tasks from selecting the asset.
4. Restore the asset before the retention period expires, or permanently clean
   it only after the server rechecks every reference.

Published assets must be unpublished before deletion. Assets referenced by
drafts, published workflows, active jobs, run snapshots, or audit records are
protected. Permanent cleanup keeps an audit tombstone and never reuses the
asset ID.

## Authorization

Every read, download, preview, reuse, and lifecycle action checks account scope,
workflow/API-key scope, and the current asset state. Public publication does
not expose the original owner, prompt, provider key, or generation parameters.
External workflow callers may pass an asset ID only when the published workflow
and its API key are authorized to use it.

## Limits

The API applies MIME/signature validation, per-file and batch limits, thumbnail
budgets, and storage warnings. These are configurable operational limits, not
permission bypasses. A failed cleanup remains queryable and can be retried; it
never triggers a new model generation.
