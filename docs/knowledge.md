# Knowledge retrieval

The knowledge feature stores normalized document versions and their chunks in
the application database, and stores embeddings in Qdrant. The embedding
provider is configured through the normal model/channel catalog; the Qdrant
endpoint, namespace, and key are supplied by Knowledge:Qdrant:*.

## Deployment configuration

The base Compose quick start disables knowledge indexing. To enable it, run
Qdrant in the **same loopback network namespace** as the API and configure:

| Setting | Development example | Production example |
| --- | --- | --- |
| `Knowledge:Enabled` | `true` | `true` |
| `Knowledge:Qdrant:Endpoint` | `http://127.0.0.1:6333` | `http://127.0.0.1:6333` |
| `Knowledge:Qdrant:Namespace` | `cfdev_local` | `cfprod_main` |
| `Knowledge:Qdrant:ApiKey` | Private Qdrant key | Private Qdrant key |

The namespace suffix must contain 4–32 lowercase letters, digits, or underscores.
Development may use another loopback port; Production requires 6333.
`localhost`, HTTPS, remote IPs, paths, and ordinary Docker service names are
not accepted by the current adapter. For source development, a Qdrant container
published only to the host's `127.0.0.1:6333` works with the host-run API.
On Linux, the host-network API deployment in [deployment](deployment.md) can
use a loopback-bound Qdrant on the same host. A bridge-network API container's
`127.0.0.1` refers to that container, not the host.

An optional, digest-pinned Podman/Quadlet reference is provided under
`deploy/qdrant/`; review its paths, secret names, file ownership, and memory
limits before installation. Its `Pull=never` setting requires pre-pulling the
referenced image. Do not open Qdrant publicly to bypass a networking problem.

After Qdrant is ready, configure an embedding channel/model in the admin UI,
allow its exact request endpoint, run the embedding test, and save the measured
dimension before creating a library. Document and query embeddings must use
the same model. Remote providers may charge for indexing and queries.

## Ingestion

1. Create a knowledge library and select a saved embedding model.
2. Upload UTF-8 text or Markdown documents.
3. Preview the semantic chunks before indexing.
4. Confirm the version; the index job embeds chunks and records its status.
   Inspect failure/checkpoint state before choosing an offered retry; do not
   assume every upstream failure can resume without another embedding call.
5. Activate a completed version explicitly.

Chunking first recognizes headings, paragraphs, lists, code blocks, and tables,
then combines semantic blocks to the target size. Only an overlong paragraph is
split by sentence; character-level splitting is the last resort and is marked
as a continuation. Each chunk retains its title path, source range, and source
offset so citations can point back to the normalized document.

## Library management

Administrators can edit a library's name and description in **Knowledge settings**.
Updates carry the displayed `revision`; a concurrent change returns HTTP 409 and
requires an explicit reload. Renaming does not re-embed documents or change pinned
workflow versions. Before/after metadata and the administrator are audited.

Deletion starts with an impact preview. All persistent pins are blockers: workflow
drafts, templates, deployment versions, and runs including historical runs. Disabling
an API or a library does not release those references. Queued, embedding, writing,
or cleaning index tasks must finish first. Confirmation requires the exact current
name, a reason, revision, and the preview's impact token. The transaction recomputes
the impact; it never trusts the client preview. New graph references and index
recovery cannot enter a library once deletion is accepted.

Accepted deletion disables the library and clears its active pointer. The existing
index worker deletes each fixed Qdrant collection before removing local chunks,
vectors, membership rows, and unreferenced document bodies. Failures retain the
remaining data and enter `delete_failed`; an administrator must recheck and retry.
Completion retains a metadata tombstone and audits. The **Deleted** list opens
these records for audit only. It does not restore deleted source documents.

**Export** downloads all readable versions or one selected version as a ZIP.
`manifest.json` maps original names to numbered safe paths and includes content
SHA-256 values and version/chunking metadata. Text is the normalized UTF-8 source.
The archive excludes credentials, access grants, embedding snapshots, and vectors;
it is a source archive rather than a complete database/vector backup. Exports use a
private temporary stream removed on close, serialize one bounded version at a time,
and audit the requested scope. Disabled or deleting libraries cannot export bodies.

**Version comparison** shows added, removed, changed, and unchanged documents,
actual byte/chunk counts, and chunking/index layout changes. Individual documents
have a paged plain-text line comparison. Large changed regions use an explicitly
marked block replacement instead of an unbounded quadratic comparison. Both
versions must belong to the current readable library, and comparison never calls
a model or changes the active version.

Management endpoints under `/api/v1/admin/knowledge-bases/{id}`:

| Method | Path | Operation |
| --- | --- | --- |
| PATCH | root | Edit name/description with revision |
| GET | `/deletion` | Current deletion impact and blockers |
| DELETE | root | Confirm or retry asynchronous deletion |
| GET | `/audit` | Latest 100 metadata/deletion/export audit records |
| GET | `/export?versionId=...` | ZIP; omit versionId for all readable versions |
| GET | `/compare?from=...&to=...` | Document and configuration summary |
| GET | `/compare/document?from=...&to=...&name=...&offset=0&limit=100` | Paged source line differences |

The administrator list accepts `includeDeleted=true` for tombstones; normal catalogs
exclude deleted libraries. Migration `0047` adds the lifecycle, audit, and reference
guards. Apply it before running this code; migrations `0001`–`0045` and their frozen
checksum file remain unchanged.

## Retrieval

The retrieval settings select vector, keyword, or hybrid search; the default
mode is `vector`, not hybrid. Hybrid search combines lexical and vector
candidates with reciprocal rank fusion. Reranking is optional and requires a
configured model and separate endpoint allowlist; it does not happen merely
because an embedding model is configured. Rerank failure follows the configured
fail/fallback policy, and score types must not be compared as if they shared a scale.

Workflow nodes pin a library/version and revalidate permissions; activating a
new library version does not silently replace that snapshot.
Retrieval checks library/version ownership and reads source text from the
database before delivering context.

The node controls `topK`, `neighborRadius`, and `maxContextChunks`.
Primary hits come first, then deduplicated neighboring chunks from the same
document. The final list is limited by **chunk count**, without cutting the
body of a retained chunk. `maxContextChunks` defaults to 8 and allows 1–32.
This is not an unlimited model context: the model's own context window,
transport and output limits still apply.

The answer question may differ from the retrieval query. Context must still
originate from a valid retrieval result; library, version, chunk IDs, exact source
text and resource access are checked. No hits produce an ungrounded fallback;
an invalid citation blocks delivery. A successful run with `grounded:false`
is not evidence that a useful answer was found.

## Document limits

The current parser accepts UTF-8 TXT/Markdown, not PDF, DOCX, spreadsheets,
images or OCR input. Each full version has its own capacity budget.
Preview shows normalized chunks; indexing and query calls may incur provider fees.

The environment JSON files define these defaults; private configuration may override
them within the validated implementation bounds:

| `Knowledge:Quotas` | Development | Production | Maximum accepted |
| --- | --- | --- | --- |
| `MaxDocuments` | 32 | 128 | 128 per version |
| `MaxDocumentBytes` | 256 KiB | 1 MiB | 1 MiB |
| `MaxVersionBytes` | 512 KiB | 2 MiB | 2 MiB |
| `MaxChunks` | 1,024 | 8,192 | 8,192 |
| `MaxLibraryReservedBytes` | 64 MiB | 2 GiB | 2 GiB logical reservation |

If no environment quota overrides are loaded, the code fallback is more
restrictive (8 documents, 64 KiB/version, 128 chunks). Check the environment
and the server-provided limits when the UI shows an unexpected cap; editing a
frontend label cannot increase server capacity. These limits are validated by
[`KnowledgeQuotas`](../src/CreativeFlow.Application/Features/Knowledge/KnowledgeQuotas.cs).

## Security and limits

Embedding endpoints use a deployment allowlist; Qdrant uses the stricter
loopback and namespace checks described above.
Do not place production documents in a development collection. Keep Qdrant
private, enable authentication, and use a dedicated namespace per environment.
Apply file-size, document-count, chunk-count, and request-timeout limits at the
API boundary; do not depend on a model provider to enforce them.
