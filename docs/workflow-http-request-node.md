# HTTP request node

The typed workflow HTTP node calls an allowlisted external endpoint. Its normal
response mode exposes `statusCode`, `headers`, `body`, and selected JSON-pointer
fields; fields-only mode exposes the declared extraction fields. This is the
general orchestration integration point for external HTTP operations, not a
replacement for Agent MCP tools or model protocols.

## Configure a node

1. Add an HTTP request node and define its input fields.
2. Select the method and enter a complete URL.
3. Reference upstream values with double-brace variables such as
   {{question}} in query parameters, headers, paths, or JSON bodies.
4. Select a credential version when the target requires authentication.
5. Connect the response fields to downstream nodes and test the node before
   publishing the workflow.

Credentials are separate from provider API keys. A credential grants only the
exact method and path that was approved; holding a credential does not grant
access to the whole host. Published workflows keep the credential version in
their immutable snapshot.

## Request and response limits

JSON, text, and URL-encoded forms are supported. Multipart uploads, binary
request bodies, and streamed upstream responses are not part of this node.
The upstream response body is limited to 1 MiB. Parsed JSON is subject to
bounded object depth, array length, field count, and extraction limits.
That is a maximum, not a promise every downstream string field accepts 1 MiB;
the node's response budget and declared downstream field limits also apply.

By default non-2xx responses fail the node. A workflow can opt into passing the
error response downstream for conditional handling. Requests are not retried
automatically when the result is unknown; create a new run only after checking
the external system.

## Security

Endpoint policy validates the scheme, DNS results, private-address rules, and
the configured allowlist. Production should allow only the exact HTTPS paths
required by the workflow. Development loopback access must be explicitly
listed and must never be copied into production.

The network setting `Workflow:HttpRequests:AllowedEndpoints` defaults to
public `https://*` when absent. Configure it explicitly for restricted
deployments; credential method/path grants remain a separate check.

Credential values are encrypted at rest, masked in logs and snapshots, and
never returned by the catalog APIs. Changing a credential creates a new version
so existing published runs remain deterministic.

## Example

The importable [example](examples/http-request.workflow.json) contains a
start node, an HTTP request, and an end node. Replace the example URL and
create a matching credential before publishing.
