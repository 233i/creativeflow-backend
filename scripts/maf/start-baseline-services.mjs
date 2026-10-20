import assert from "node:assert/strict";
import { spawn, execFileSync } from "node:child_process";
import { closeSync, openSync } from "node:fs";
import { createServer } from "node:net";
import { once } from "node:events";

const root = process.env.CREATIVEFLOW_MAF_TEST_ROOT;
assert.match(root ?? "", /^\/(private\/)?tmp\/creativeflow-maf-[\w.]+$/);
for (const port of [55438, 55439, 55440, 55441, 55442, 55443, 55444, 55445]) {
  const probe = createServer();
  await new Promise((resolve, reject) => { probe.once("error", reject); probe.listen(port, "127.0.0.1", resolve); });
  await new Promise(resolve => probe.close(resolve));
}
const data = `${root}/data`, runtime = `${root}/runtime`;
const erlang = `${runtime}/erlang@28/28.5.0.6/lib/erlang`;
const run = (command, args) => execFileSync(command, args, { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }).trim();
const children = [];
let postgres = false, stopping = false, rabbitStarted = false;
async function stop() {
  if (stopping) return;
  stopping = true;
  await Promise.all(children.map(async child => {
    if (child.exitCode !== null || child.signalCode !== null) return;
    const exited = once(child, "exit"); child.kill("SIGTERM"); await exited;
  }));
  if (postgres) run("pg_ctl", ["-D", `${data}/postgres`, "-m", "fast", "-w", "stop"]);
  // 只关闭这组专用端口启动的 epmd；Rabbit 退出后它不应再有注册节点。
  // Rabbit 若在启动 epmd 前失败，该端口可以不存在；不能因此掩盖原始启动错误。
  if (rabbitStarted) {
    try { run(`${erlang}/bin/epmd`, ["-port", "55441", "-kill"]); }
    catch (error) {
      if (!String(error.stderr).includes("Cannot connect to local epmd")) throw error;
    }
  }
}
try {
  run("pg_ctl", ["-D", `${data}/postgres`, "-l", `${data}/postgres.log`, "-o", `-h 127.0.0.1 -p 55439 -k ${data}`, "-w", "start"]); postgres = true;
  const psql = ["-h", "127.0.0.1", "-p", "55439", "-U", "creativeflow_maf_test"];
  for (const database of ["creativeflow_maf_tests", "creativeflow_maf_e2e"]) {
    const exists = run("psql", [...psql, "-d", "postgres", "-At", "-c", `SELECT 1 FROM pg_database WHERE datname='${database}'`]);
    if (!exists) run("createdb", [...psql, "--encoding=UTF8", "--template=template0", database]);
  }
  const launch = (binary, args, env, logName) => {
    const log = openSync(`${data}/${logName}`, "a");
    const child = spawn(binary, args, { env: { ...process.env, ...env }, stdio: ["ignore", log, log] });
    closeSync(log);
    children.push(child); return child;
  };
  const redis = launch(`${runtime}/redis/8.10.1/bin/redis-server`, ["--bind", "127.0.0.1", "--port", "55438", "--save", "", "--appendonly", "no"], {}, "redis.log");
  const rabbit = launch(`${runtime}/rabbitmq/4.3.5/libexec/rabbitmq-server`, [], {
    PATH: `${erlang}/bin:/opt/homebrew/bin:/usr/bin:/bin`, ERL_ROOTDIR: erlang,
    ERL_EPMD_PORT: "55441", ERL_EPMD_ADDRESS: "127.0.0.1", RABBITMQ_NODENAME: "creativeflow_maf_p01@localhost",
    RABBITMQ_CONFIG_FILE: `${data}/rabbitmq.conf`, RABBITMQ_MNESIA_BASE: `${data}/mnesia`, RABBITMQ_LOG_BASE: `${data}/rabbit-logs`,
    RABBITMQ_PID_FILE: `${data}/rabbit.pid`, RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS: "+S 2:2 +A 4 -setcookie creativeflow-maf-fixture-only",
  }, "rabbit.log");
  rabbitStarted = true;
  console.log(JSON.stringify({ root, redisPid: redis.pid, rabbitPid: rabbit.pid, ports: [55438, 55439, 55440], cleanup: "SIGINT/SIGTERM this process after stopping API/mock" }));
  for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, () => { void stop().then(() => process.exit(0)); });
  for (const child of children) child.on("exit", () => { if (!stopping) void stop().then(() => process.exit(1)); });
} catch (error) { await stop(); throw error; }
