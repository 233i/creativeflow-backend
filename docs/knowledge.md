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
4. Confirm the version; the index job embeds each chunk and records a
   checkpoint so a failed job can resume.
5. Activate a completed version explicitly.

Chunking first recognizes headings, paragraphs, lists, code blocks, and tables,
then combines semantic blocks to the target size. Only an overlong paragraph is
split by sentence; character-level splitting is the last resort and is marked
as a continuation. Each chunk retains its title path, source range, and source
offset so citations can point back to the normalized document.

## Retrieval

Search combines lexical matching and vector similarity, then applies reciprocal
rank fusion and optional reranking. Results are filtered by account, library,
active version, and permissions. Neighboring chunks from the same document may
be added as supporting context; the final context is trimmed by relevance when
the model budget is reached.

Knowledge-answer nodes receive the original question and a separate retrieval
query. The answer must cite chunks from the current retrieval result; missing
or invalid citations block delivery instead of silently fabricating grounding.

## Security and limits

Embedding endpoints use a deployment allowlist; Qdrant uses the stricter
loopback and namespace checks described above.
Do not place production documents in a development collection. Keep Qdrant
private, enable authentication, and use a dedicated namespace per environment.
Apply file-size, document-count, chunk-count, and request-timeout limits at the
API boundary; do not depend on a model provider to enforce them.
