// What an outside assistant sees through MCP (README.md): a minimal tool loop of an OpenAI-compatible chat model over the instance's /mcp,
// with the tool set of your choice, scored like the other runs (score.mjs). The in-app agent offers only the atlas_* tools of its groups,
// so this is the way to measure whether projector_get_requirements and friends change the answers.
//   $env:ATLAS_EVAL_PASSWORD = '...'
//   node tests/ai-eval/projector/mcp-agent.mjs --url http://127.0.0.1:5171 --chat http://127.0.0.1:8080/v1 --model qwen3.8-flash-next-iq3_s \
//     --tools atlas|projector|deploy --map <map.json> --out <report.json> [--cases tests/ai-eval/projector/cases.json]
import { readFileSync, writeFileSync } from "node:fs";
import { scoreAnswer } from "./score.mjs";

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, index, all) =>
  value.startsWith("--") ? [...pairs, [value.slice(2), all[index + 1]]] : pairs, []));
const base = args.url.replace(/\/$/, "");
const chat = args.chat.replace(/\/$/, "");
const set = JSON.parse(readFileSync(args.cases ?? new URL("./cases.json", import.meta.url), "utf8"));
const map = JSON.parse(readFileSync(args.map, "utf8").replace(/^﻿/, ""));
const pathOf = new Map(map.map((page) => [page.id, page.path]));
// --tools deploy runs only the cases with write: true (they create a draft deployment page, token scope draft); the other tool sets skip them.
const deploy = args.tools === "deploy";
const cases = set.cases.filter((item) => Boolean(item.write) === deploy);
const offered = new Set(["atlas_search", "atlas_get_page", "atlas_list_spaces", ...(args.tools === "projector" ? ["projector_get_requirements", "projector_waiting", "projector_overview", "projector_get_catalog"] : []),
  ...(deploy ? ["projector_overview", "projector_get_catalog", "projector_create_deployment", "projector_insert_requirements"] : [])]);

// Session sign-in only to mint a read-only token for /mcp; the token is never printed.
const cookies = new Map();
async function web(method, path, body) {
  const csrf = method === "GET" ? null : (await web("GET", "/api/auth/csrf")).token;
  const headers = { accept: "application/json", cookie: [...cookies].map(([n, v]) => `${n}=${v}`).join("; ") };
  if (body !== undefined) headers["content-type"] = "application/json";
  if (csrf) headers["x-csrf-token"] = csrf;
  const response = await fetch(base + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  for (const line of response.headers.getSetCookie()) { const [pair] = line.split(";"); const at = pair.indexOf("="); cookies.set(pair.slice(0, at), pair.slice(at + 1)); }
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${text.slice(0, 200)}`);
  return text ? JSON.parse(text) : null;
}
await web("POST", "/api/auth/login", { username: process.env.ATLAS_EVAL_USER ?? "eval-admin", password: process.env.ATLAS_EVAL_PASSWORD });
// A local evaluation instance only: switch MCP on and let the evaluation administrator issue a read token (draft for --tools deploy).
const me = await web("GET", "/api/auth/me");
const access = await web("GET", `/api/admin/users/${me.id}/api-access`);
const level = deploy ? "write" : "read";
if (!access.canIssueTokens || access.level === "none" || (deploy && access.level === "read")) await web("PUT", `/api/admin/users/${me.id}/api-access`, { canIssueTokens: true, level, expectedVersion: access.version });
const mcp = await web("GET", "/api/admin/mcp");
if (!mcp.enabled) await web("PUT", "/api/admin/mcp", { enabled: true, expectedVersion: mcp.version });
const secret = (await web("POST", "/api/profile/api-tokens", { name: "projector-eval-mcp", scopes: deploy ? ["read", "write"] : ["read"], channels: ["mcp"], expiresInDays: 1 })).secret;

let sessionId = null; let rpcId = 0;
async function rpc(method, params, notification = false) {
  const response = await fetch(base + "/mcp", { method: "POST", headers: {
    authorization: `Bearer ${secret}`, "content-type": "application/json", accept: "application/json, text/event-stream", ...(sessionId ? { "mcp-session-id": sessionId } : {}) },
  body: JSON.stringify({ jsonrpc: "2.0", ...(notification ? {} : { id: ++rpcId }), method, params }) });
  sessionId ??= response.headers.get("mcp-session-id");
  const text = await response.text();
  if (!response.ok) throw new Error(`mcp ${method}: ${response.status} ${text.slice(0, 200)}`);
  if (notification || !text) return null;
  const json = text.startsWith("{") ? text : text.split("\n").filter((line) => line.startsWith("data:")).map((line) => line.slice(5).trim()).pop();
  const message = JSON.parse(json);
  if (message.error) throw new Error(`mcp ${method}: ${JSON.stringify(message.error)}`);
  return message.result;
}
await rpc("initialize", { protocolVersion: "2025-03-26", capabilities: {}, clientInfo: { name: "projector-eval", version: "1" } });
await rpc("notifications/initialized", {}, true);
const tools = (await rpc("tools/list", {})).tools.filter((tool) => offered.has(tool.name));
const functions = tools.map((tool) => ({ type: "function", function: { name: tool.name, description: tool.description, parameters: tool.inputSchema } }));

const system = "Jsi asistent firemní wiki Atlas. Odpovídej česky, stručně a jen podle výsledků nástrojů. Když wiki odpověď neobsahuje, řekni to a nic nevymýšlej. " +
  "U každé odpovědi uveď názvy stránek, ze kterých čerpáš.";
const results = [];
for (const item of cases) {
  const started = performance.now();
  const messages = [{ role: "system", content: system }, { role: "user", content: item.query }];
  const calls = []; const toolCalls = []; const pages = new Set(); let answer = "";
  try {
    for (let step = 0; step < 8; step++) {
      const response = await fetch(chat + "/chat/completions", { method: "POST", headers: { "content-type": "application/json", authorization: "Bearer local" },
        body: JSON.stringify({ model: args.model, messages, tools: functions, temperature: 0.2, reasoning_effort: "none" }) });
      if (!response.ok) throw new Error(`chat ${response.status} ${(await response.text()).slice(0, 200)}`);
      const message = (await response.json()).choices[0].message;
      messages.push({ role: "assistant", content: message.content ?? "", ...(message.tool_calls ? { tool_calls: message.tool_calls } : {}) });
      if (!message.tool_calls?.length) { answer = message.content ?? ""; break; }
      for (const call of message.tool_calls) {
        const input = JSON.parse(call.function.arguments || "{}");
        calls.push(call.function.name); toolCalls.push({ name: call.function.name, input });
        if (input.pageId) pages.add(input.pageId);
        let output;
        try {
          const result = await rpc("tools/call", { name: call.function.name, arguments: input });
          output = (result.content ?? []).map((part) => part.text ?? "").join("\n");
          // Pages that a search returned count as seen, like the sources of an in-app answer.
          for (const match of output.matchAll(/pageId: ([0-9a-f-]{36})/g)) pages.add(match[1]);
        } catch (error) { output = String(error.message); }
        messages.push({ role: "tool", tool_call_id: call.id, content: output.slice(0, 24000) });
      }
    }
  } catch (error) { answer = `ERROR ${error.message}`; }
  const seen = [...pages].map((id) => pathOf.get(id)).filter(Boolean);
  // Citations are what the answer names; for the score a page counts as cited when its title is in the text.
  const titles = new Map(map.map((page) => [page.path, page.title]));
  const cited = [...titles].filter(([, title]) => answer.toLowerCase().includes(title.toLowerCase())).map(([path]) => path);
  results.push({ id: item.id, calls, toolCalls, ms: Math.round(performance.now() - started), answer, cited, seen, ...scoreAnswer(item, { answer, cited, sources: seen, toolCalls }) });
  process.stdout.write(".");
}
console.log();
const rate = (pick) => Math.round((1000 * results.filter(pick).length) / results.length) / 10;
const summary = { tools: args.tools ?? "atlas", cases: results.length, answerOk: rate((row) => row.answerOk), citedOk: rate((row) => row.citedOk !== false) };
writeFileSync(args.out, JSON.stringify({ summary, results }, null, 2));
console.log(JSON.stringify(summary));
