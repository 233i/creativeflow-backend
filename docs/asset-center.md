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
4. Restore an asset while it has not been permanently purged. The retention
   period is the minimum wait before cleanup, not an automatic deletion timer.
   Permanent cleanup rechecks references and permissions.

Published assets must be unpublished before deletion. Active jobs and protected
references (canvases, workflow drafts, deployments, templates and valid external
uploads) block permanent cleanup. Historical references preserve the original
by default; a separate explicit confirmation is required for eligible historical
outputs. The deletion preview reports the actual blockers. Permanent cleanup
keeps an audit tombstone and never reuses the asset ID.

Agent tool receipts, assistant replies and execution events are observations.
They can record a canvas that still mentions recycled or purged assets without
restoring those assets or granting access to their bytes. Migration 0046 keeps
these audit references while applying the existing admission check to tool
inputs and new user image attachments. Workflow and generation admission,
asset permissions, recycling and permanent-cleanup protections are unchanged.

## Authorization

Each read, download, preview, reuse, and lifecycle action checks its applicable
account/admin/workflow/API-key authorization and the current asset state; these
are different entry points, not interchangeable credentials. Public publication does
not expose the original owner, prompt, provider key, or generation parameters.
External workflow callers may pass an asset ID only when the published workflow
and its API key are authorized to use it.

## Limits

The API applies MIME/signature validation, per-file and batch limits, thumbnail
budgets, and storage warnings. These are configurable operational limits, not
permission bypasses. A failed cleanup remains queryable and can be retried; it
never triggers a new model generation.

Capacity warnings do not automatically purge files. Logical asset totals can
exceed physical storage because content is deduplicated; neither count is a
measurement of immediately reclaimable host disk space.
