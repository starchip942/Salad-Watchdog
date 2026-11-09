// End-to-end test against `wrangler dev`: fake agent + fake viewer + website calls.
import WebSocket from "ws";
import crypto from "node:crypto";
const BASE = "http://127.0.0.1:8787", WS = "ws://127.0.0.1:8787";
const KEY = "test-agent-key-0123456789abcdef0123456789abcdef";
const assert = (c, m) => { if (!c) { console.error("FAIL:", m); process.exit(1); } console.log("ok -", m); };
const wait = ms => new Promise(r => setTimeout(r, ms));
const post = (p, body, token, ip) => fetch(BASE + p, { method: "POST", headers: { "content-type": "application/json",
  ...(token ? { authorization: "Bearer " + token } : {}), ...(ip ? { "CF-Connecting-IP": ip } : {}) }, body: JSON.stringify(body) });

// agent with wrong key is refused
const bad = new WebSocket(WS + "/agent", { headers: { Authorization: "Bearer nope" } });
const badRes = await new Promise(r => { bad.on("unexpected-response", (_q, res) => r(res.statusCode)); bad.on("open", () => r(101)); });
assert(badRes === 403, "agent with wrong key refused");

const agent = new WebSocket(WS + "/agent", { headers: { Authorization: "Bearer " + KEY } });
const agentMsgs = [];
agent.on("message", d => agentMsgs.push(JSON.parse(d)));
await new Promise(r => agent.on("open", r));
await wait(300);
assert(agentMsgs[0]?.type === "hello", "agent gets hello");

// set password hash (what the PC does on first run)
const salt = crypto.randomBytes(16), iter = 100000;
const hash = crypto.pbkdf2Sync("test-pass-1", salt, iter, 32, "sha256");
agent.send(JSON.stringify({ type: "password_hash", salt: salt.toString("base64"), hash: hash.toString("base64"), iter, ver: 1 }));
await wait(300);
assert(agentMsgs.some(m => m.type === "password_ack" && m.ok), "password hash accepted");

agent.send(JSON.stringify({ type: "event", event: { id: "e1", t: 1000, level: "info", msg: "Agent started" } }));
const viewer = new WebSocket(WS + "/view");
const vm = []; viewer.on("message", d => vm.push(JSON.parse(d)));
await new Promise(r => viewer.on("open", r)); await wait(300);
assert(vm[0]?.type === "hello" && vm[0].online === true, "viewer hello shows online");
assert(vm[0].events.length === 1, "viewer gets recent events");
assert(agentMsgs.some(m => m.type === "viewers" && m.n === 1), "agent told viewer count");
agent.send(JSON.stringify({ type: "telemetry", data: { v: 1, t: 2000, gpu: { tempC: 61, fans: [{ pct: 40 }] } } }));
await wait(300);
assert(vm.some(m => m.type === "telemetry" && m.data.gpu.tempC === 61), "viewer receives live telemetry");
viewer.send(JSON.stringify({ type: "command", cmd: "pc_off_now" })); await wait(200);
assert(!agentMsgs.some(m => m.type === "command"), "viewer channel cannot send commands");

let r = await post("/command", { cmd: "salad_pause" });
assert(r.status === 401, "command without token refused");

// wrong password x5 -> locked
for (let i = 0; i < 5; i++) r = await post("/auth", { password: "000000" }, null, "1.2.3.4");
let j = await r.json();
assert(r.status === 401 && j.retryAfter > 0, "5th wrong attempt triggers lockout");
r = await post("/auth", { password: "test-pass-1" }, null, "1.2.3.4");
assert(r.status === 429, "even the right password is refused while locked out");
r = await post("/auth", { password: "test-pass-1" }, null, "5.6.7.8"); j = await r.json();
assert(r.status === 200 && j.token, "correct password gives a session token");
const token = j.token;

agent.removeAllListeners("message");
agent.on("message", d => {
  const m = JSON.parse(d);
  if (m.type !== "command") return;
  const data = [m.id, m.cmd, m.args, m.ts, m.nonce].join("|");
  const keyHash = crypto.createHash("sha256").update(KEY).digest("hex");
  const ok = [KEY, keyHash].some(k => crypto.createHmac("sha256", k).update(data).digest("hex") === m.sig);
  agent.send(JSON.stringify({ type: "result", id: m.id, ok, msg: ok ? "done " + m.cmd : "bad sig" }));
});
r = await post("/command", { cmd: "salad_pause" }, token); j = await r.json();
assert(j.ok && j.msg === "done salad_pause", "signed command reaches agent and result comes back");
r = await post("/command", { cmd: "rm_rf" }, token);
assert(r.status === 400, "unknown command refused");

// password change invalidates sessions
const salt2 = crypto.randomBytes(16);
agent.send(JSON.stringify({ type: "password_hash", salt: salt2.toString("base64"), hash: crypto.pbkdf2Sync("new-pass-1", salt2, iter, 32, "sha256").toString("base64"), iter, ver: 2 }));
await wait(300);
r = await post("/command", { cmd: "salad_pause" }, token);
assert(r.status === 401, "old session token invalid after password change");
r = await post("/auth", { password: "test-pass-1" }, null, "9.9.9.9");
assert(r.status === 401, "old password no longer works");
r = await post("/auth", { password: "new-pass-1" }, null, "9.9.9.9");
assert(r.status === 200, "new password works");

r = await fetch(BASE + "/events?limit=10"); j = await r.json();
assert(j.events.length === 1 && j.events[0].id === "e1", "event history endpoint");

agent.close(); await wait(400);
assert(vm.some(m => m.type === "status" && m.online === false), "viewer told when PC goes offline");
viewer.close();
console.log("ALL RELAY TESTS PASSED");
process.exit(0);
