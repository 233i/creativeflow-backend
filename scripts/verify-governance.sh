#!/usr/bin/env bash
set -euo pipefail

# 始终在仓库根目录运行，让 SDK 选择遵守 global.json，避免调用目录影响结果。
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_bin="${CREATIVEFLOW_DOTNET_BIN:-dotnet}"
cd "$repository_root"

# 可指定隔离安装的 dotnet，但不改动锁定版本或允许 SDK 自动升级。
"$dotnet_bin" --version
"$dotnet_bin" build CreativeFlow.slnx -c Release --nologo
"$dotnet_bin" run --project tools/CreativeFlow.CodeQuality -c Release --no-build -- "$repository_root"
"$dotnet_bin" test tests/CreativeFlow.UnitTests/CreativeFlow.UnitTests.csproj -c Release --no-build --nologo \
    --logger 'trx;LogFileName=unit-tests.trx' \
    --results-directory "$repository_root/artifacts/governance/test-results"

# 只生成现有 MVC/JIT 发布产物；不启动服务，不执行数据库迁移或后台任务。
"$dotnet_bin" publish src/CreativeFlow.Api/CreativeFlow.Api.csproj -c Release --no-build --no-restore --nologo \
    --output "$repository_root/artifacts/governance/publish"
git diff --check
