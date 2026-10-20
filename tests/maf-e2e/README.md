# Isolated MAF integration harness

These tests run the real Release API, PostgreSQL repositories, and workers
against synthetic loopback HTTP model fixtures. They do not require the
frontend or paid model services.

## Isolation requirements

- Use only a disposable test database and a dedicated `/tmp/creativeflow-maf-*`
  directory. Never point this harness at a development or production database.
- The API runs under `MigrationBaseline` on `127.0.0.1:55443`.
  Preflight checks reject a normal application environment.
- The macOS `sandbox-exec` policy blocks non-loopback outbound connections.
- Test keys, admin passwords, and prompts are synthetic. Never reuse them in
  a deployment. The `.invalid` media origin does not resolve publicly.
- Reserve ports 55438–55445. Scripts refuse to take over occupied ports.
- Keep authentication rate limiting intact and wait at least 60 seconds
  between account-creation HTTP batches.

## Prepare dependencies

The supplied launcher targets **macOS arm64**, with PostgreSQL 18, Node,
OpenSSL 3, ffmpeg, and the SDK pinned in `global.json`.
It extracts cached Homebrew bottles for Redis 8.10.1, RabbitMQ 4.3.5, and
Erlang 28.5.0.6 into a temporary directory; it does not install or enable
system services. If those exact caches are absent, prepare/review the test
runtime before using the launcher. Do not silently substitute new versions.

~~~bash
export CREATIVEFLOW_DOTNET_BIN="$(command -v dotnet)"
node scripts/maf/prepare-baseline-env.mjs
~~~

Record the returned directory and use it consistently in every terminal:

~~~bash
export CREATIVEFLOW_MAF_TEST_ROOT=/tmp/creativeflow-maf-p01.REPLACE_WITH_OUTPUT
node scripts/maf/start-baseline-services.mjs
~~~

Keep this process running. It creates only `creativeflow_maf_tests` and
`creativeflow_maf_e2e` in its own cluster.

## Run verification

In a second terminal, with the same environment variables:

~~~bash
export CREATIVEFLOW_TEST_POSTGRES='Host=127.0.0.1;Port=55439;Database=creativeflow_maf_tests;Username=creativeflow_maf_test;Pooling=false'
bash scripts/verify-maf-migration.sh
npm ci --prefix tests/maf-e2e
node tests/maf-e2e/fixture-server.mjs
~~~

Keep the fixture server running. Run process-restart tests before starting
a persistent API:

~~~bash
node --test tests/maf-e2e/restart.test.mjs
~~~

Restart tests own and stop their API child processes. They include a real
90-second lease wait and verify recovery without duplicate model calls.
Then start the persistent API in another terminal:

~~~bash
node scripts/maf/start-baseline-api.mjs
~~~

Run the HTTP and SignalR contract suite:

~~~bash
node --test --test-concurrency=1 tests/maf-e2e/contracts.test.mjs
~~~

Synthetic markers such as `[unknown]`, `[hold]`, and `[downloadfail]`
exercise uncertain provider results, cancellation, and recovery. They are
fixture conventions, not product parameters.

## Performance checks

Use `benchmark.mjs` to collect a report on the same machine and isolated
runtime. Set `CREATIVEFLOW_MAF_API_PID` only to the PID of the API you started.
Use `CREATIVEFLOW_MAF_REFERENCE_REPORT` with `runtime-performance.mjs` to
compare against your own saved baseline. Do not interpret checked-in fixture
numbers as production capacity or claim historical binaries are available.

Do not run benchmarks alongside other generation tests or builds.
Generated reports belong under ignored `artifacts/`, not in Git.

## Stop

Stop the exact API/fixture processes you started, then send SIGINT/SIGTERM
to the `start-baseline-services.mjs` process. It stops only its own processes
and temporary PostgreSQL cluster, not unrelated services, and retains data
for inspection. Never use broad `pkill node` or `pkill dotnet` commands.
