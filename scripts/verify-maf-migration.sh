#!/usr/bin/env bash
set -euo pipefail

# 迁移离线门禁：数据库缺失不能以跳过用例的方式冒充兼容通过。
# 不创建数据库服务，不启动 API，不连接模型，也不替代浏览器、性能及回退验收。
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_bin="${CREATIVEFLOW_DOTNET_BIN:-dotnet}"
if [[ -z "${CREATIVEFLOW_TEST_POSTGRES:-}" ]]; then
    echo "CREATIVEFLOW_TEST_POSTGRES must point to an explicitly isolated test database." >&2
    exit 2
fi
cd "$repository_root"

# TRX 没有完整执行或出现跳过时失败；不输出连接串等敏感配置。
assert_complete_test_run() {
    local line
    while IFS= read -r line; do
        if [[ "$line" == *'<Counters '* ]]; then
            if [[ "$line" =~ total=\"([1-9][0-9]*)\" ]] &&
               [[ "$line" == *'failed="0"'* ]] &&
               [[ "$line" == *'error="0"'* ]] &&
               [[ "$line" == *'aborted="0"'* ]] &&
               [[ "$line" == *'notExecuted="0"'* ]]; then
                return 0
            fi
            break
        fi
    done < "$1"
    echo "Incomplete migration test run: $1" >&2
    return 1
}

# 保持原治理命令原样；没有忽略或屏蔽原有规范、架构和 SQL 校验。
"$dotnet_bin" restore src/CreativeFlow.Infrastructure/CreativeFlow.Infrastructure.csproj --locked-mode --nologo
"$dotnet_bin" restore src/CreativeFlow.Api/CreativeFlow.Api.csproj --locked-mode --nologo
bash scripts/verify-governance.sh
assert_complete_test_run artifacts/governance/test-results/unit-tests.trx

# SDK 行为探针仍独立执行；生产仅引用固定 SDK，不引用探针程序集。
"$dotnet_bin" restore tests/CreativeFlow.MafCompatibilityTests/CreativeFlow.MafCompatibilityTests.csproj --locked-mode --nologo
"$dotnet_bin" test tests/CreativeFlow.MafCompatibilityTests/CreativeFlow.MafCompatibilityTests.csproj \
    -c Release --no-restore --nologo --logger 'trx;LogFileName=maf-probe.trx' \
    --results-directory "$repository_root/artifacts/maf/probe"
assert_complete_test_run artifacts/maf/probe/maf-probe.trx
"$dotnet_bin" publish tools/CreativeFlow.MafProbe/CreativeFlow.MafProbe.csproj -c Release \
    --no-build --no-restore --nologo --output "$repository_root/artifacts/maf/probe-publish"
