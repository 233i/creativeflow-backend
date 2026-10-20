# Documentation

This directory contains the public operating and API documentation for the
CreativeFlow backend. Historical deployment notes, migration diaries, internal
acceptance reports, server addresses, and local incident logs are intentionally
not part of the public repository.

## Start here

| Document | Purpose |
| --- | --- |
| [Deployment](deployment.md) | Build, configure, migrate, back up, and expose an instance |
| [Configuration](configuration.md) | Configuration precedence and secret boundaries |
| [Local development](local-development.md) | Run tests and the API from source |
| [Operations](operations.md) | Production health, storage, backups, and recovery |
| [Architecture](architecture.md) | Projects, runtime boundaries, and dependencies |

## API and features

| Document | Purpose |
| --- | --- |
| [Authentication](authentication.md) | Account/admin sessions and OAuth |
| [AI/canvas API](ai-canvas-api.md) | Canvas, provider, generation, and asset contracts |
| [Workflow nodes](workflows.md) | Workflow graph and execution concepts |
| [AG-UI workflow API](ag-ui-workflow-api.md) | External workflow runs and event protocol |
| [Workflow streaming](workflow-api-streaming.md) | SSE event ordering and reconnect semantics |
| [HTTP request node](workflow-http-request-node.md) | Credentials, outbound allowlists, and response limits |
| [Knowledge retrieval](knowledge.md) | Documents, embedding, hybrid search, and citations |
| [Asset center](asset-center.md) | Persistent media metadata and reuse |
| [Image generation jobs](image-generation-jobs.md) | Asynchronous media job lifecycle |
| [SignalR](realtime-signalr.md) | Realtime groups and event delivery |
| [Device identification](device-identification.md) | Identity scope and privacy limits |

## Engineering

| Document | Purpose |
| --- | --- |
| [Data model](data-model.md) | Database boundaries and migration rules |
| [Coding standards](coding-standards.md) | Contributor conventions |
| [Runtime constraints](aot-constraints.md) | JIT/MVC and Dapper.AOT boundaries |
| [Examples](examples/README.md) | Importable workflow examples |
| [ADR directory](adr/) | Current architecture decisions |

The public repository is intentionally a clean release snapshot. Private
deployment credentials, production topology, and discarded implementation
plans belong in an internal operations repository.
