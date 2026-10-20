import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

// macOS Apple Silicon 的可重放离线环境。只解压已缓存的固定版本 bottle；不安装/启用系统服务。
assert.equal(process.platform, "darwin"); assert.equal(process.arch, "arm64");
const run = (command, args) => execFileSync(command, args, { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }).trim();
const packages = [["redis", "8.10.1"], ["erlang@28", "28.5.0.6"], ["rabbitmq", "4.3.5"]].map(([name, version]) => {
  const archive = run("brew", ["--cache", name]);
  assert.ok(existsSync(archive) && archive.includes(version), `Cache the pinned ${name} ${version} bottle first; do not silently upgrade.`);
  return { name, version, archive };
});
assert.match(run("psql", ["--version"]), /18\./);
const ssl = run("brew", ["--prefix", "openssl@3"]);
const root = mkdtempSync("/tmp/creativeflow-maf-p01.");
const data = `${root}/data`, runtime = `${root}/runtime`;
mkdirSync(data); mkdirSync(runtime);
for (const item of packages) run("tar", ["-xzf", item.archive, "-C", runtime]);
const relocate = (binary, libraries) => {
  for (const library of libraries) run("install_name_tool", ["-change", `@@HOMEBREW_PREFIX@@/opt/openssl@3/lib/${library}`, `${ssl}/lib/${library}`, binary]);
  run("codesign", ["--force", "--sign", "-", binary]);
};
relocate(`${runtime}/redis/8.10.1/bin/redis-server`, ["libssl.3.dylib", "libcrypto.3.dylib"]);
relocate(`${runtime}/erlang@28/28.5.0.6/lib/erlang/lib/crypto-5.8.3.3/priv/lib/crypto.so`, ["libcrypto.3.dylib"]);
const erlang = `${runtime}/erlang@28/28.5.0.6/lib/erlang`;
const rabbit = `${runtime}/rabbitmq/4.3.5`;
// 仅对第三方 bottle 中的安装前缀进行机械重定位，不改项目或框架运行代码。
for (const [name, replacements] of [
  ["rabbitmq-env", [[/^RABBITMQ_HOME=.*$/m, `RABBITMQ_HOME=${rabbit}`]]],
  ["rabbitmq-defaults", [[/^SYS_PREFIX=.*$/m, `SYS_PREFIX=${data}`], [/^CLEAN_BOOT_FILE=.*$/m, `CLEAN_BOOT_FILE=${erlang}/bin/start_clean`], [/^SASL_BOOT_FILE=.*$/m, `SASL_BOOT_FILE=${erlang}/bin/start_clean`]]],
]) {
  const path = `${rabbit}/libexec/${name}`;
  let source = readFileSync(path, "utf8");
  for (const [pattern, value] of replacements) { assert.match(source, pattern); source = source.replace(pattern, value); }
  writeFileSync(path, source);
}
const repository = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
writeFileSync(`${data}/rabbitmq.conf`, readFileSync(`${repository}/scripts/maf/rabbitmq.conf`));
run("initdb", ["-D", `${data}/postgres`, "-U", "creativeflow_maf_test", "--auth=trust", "--encoding=UTF8", "--no-locale"]);
run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=64x64", "-frames:v", "1", `${data}/fixture.png`]);
run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10:duration=1", "-pix_fmt", "yuv420p", `${data}/fixture.mp4`]);
console.log(JSON.stringify({ root, packages: packages.map(({ name, version }) => ({ name, version })), next: "start-baseline-services.mjs" }));
