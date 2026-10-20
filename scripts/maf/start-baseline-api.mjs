import { spawn } from "node:child_process";
import { openSync, mkdirSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

// 仅启动隔离的原 API：不修改 Program、DI、模型客户端或日常配置文件。
export function startBaselineApi() {
const root = process.env.CREATIVEFLOW_MAF_TEST_ROOT;
if (!root || !/^\/(private\/)?tmp\/creativeflow-maf-[\w.]+$/.test(root)) {
  throw new Error("CREATIVEFLOW_MAF_TEST_ROOT must be a dedicated /tmp/creativeflow-maf-* directory.");
}
const repository = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
mkdirSync(`${root}/data`, { recursive: true });
const log = openSync(`${root}/data/api.log`, "a");
const profile = '(version 1) (allow default) (deny network-outbound) (allow network-outbound (remote ip "localhost:*"))';
const binary = process.env.CREATIVEFLOW_MAF_API_BINARY ?? `${repository}/src/CreativeFlow.Api/bin/Release/net10.0/CreativeFlow.Api.dll`;
if (!resolve(binary).startsWith(`${repository}/`) || !binary.endsWith("/CreativeFlow.Api.dll")) {
  throw new Error("Only a compiled API inside this repository may be started by the isolation harness.");
}
const child = spawn("/usr/bin/sandbox-exec", ["-p", profile,
  process.env.CREATIVEFLOW_DOTNET_BIN ?? "dotnet", binary], {
  cwd: `${root}/data`, stdio: ["ignore", log, log],
  env: {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: "MigrationBaseline",
    ASPNETCORE_URLS: "http://127.0.0.1:55443",
    // macOS SBPL 的 localhost 过滤不接受 IPv4-mapped IPv6；测试服务均只绑定 IPv4 回环。
    DOTNET_SYSTEM_NET_DISABLEIPV6: "1",
    Postgres__ConnectionString: "Host=127.0.0.1;Port=55439;Database=creativeflow_maf_e2e;Username=creativeflow_maf_test",
    Postgres__RunMigrationsOnStartup: "true",
    Redis__Configuration: "127.0.0.1:55438,abortConnect=false",
    Redis__InstanceName: "maf-p01:",
    RabbitMq__HostName: "127.0.0.1", RabbitMq__Port: "55440",
    RabbitMq__UserName: "guest", RabbitMq__Password: "guest", RabbitMq__VirtualHost: "/",
    Realtime__Backplane: "None",
    // 只签发合成公网形态 URL；模型替身不下载参考图，媒体路由另外通过回环地址验签。
    Authentication__PublicOrigin: "http://media.maf.example.invalid:55444",
    Authentication__GitHub__Enabled: "false",
    Admin__DefaultUsername: "maf-admin", Admin__DefaultPassword: "Maf-p01-fixture-only!",
    DeviceIdentity__Pepper: "maf-p01-synthetic-pepper-only",
    Ai__EncryptionKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
    Ai__AssetRoot: `${root}/data/assets`,
    Skills__Root: `${root}/data/skills`, Skills__SandboxEnabled: "false",
    OpenTelemetry__OtlpEndpoint: "",
    Logging__LogLevel__Default: "Warning",
  },
});
return child;
}

if (resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const child = startBaselineApi();
  console.log(JSON.stringify({ pid: child.pid, api: "http://127.0.0.1:55443", outbound: "loopback-only" }));
  for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, () => child.kill("SIGTERM"));
  child.on("exit", code => process.exit(code ?? 1));
}
