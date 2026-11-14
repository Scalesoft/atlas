// Chunk-level retrieval of the AI index (README.md): for every case with facts, the nearest chunks of the active generation by cosine distance
// of the bge-m3 query vector, and whether a chunk that contains ALL the facts (for expectAll cases: the first chunks together) (score.mjs: alternatives, no diacritics) is among the first 1 / 3 / 5.
// Pure vector order, as the semantic half of the hybrid search sees it; read-only SQL through `podman exec <container> psql`.
//   node tests/ai-eval/projector/chunk-retrieval.mjs --container projector-eval-pg --db eval_with [--embed http://127.0.0.1:1234/v1] [--dump]
import { readFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { mentions } from "./score.mjs";

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, index, all) =>
  value.startsWith("--") ? [...pairs, [value.slice(2), all[index + 1]?.startsWith("--") ? "true" : all[index + 1]]] : pairs, []));
const embed = (args.embed ?? "http://127.0.0.1:1234/v1").replace(/\/$/, "");
const cases = JSON.parse(readFileSync(new URL("./cases.json", import.meta.url), "utf8")).cases.filter((item) => (item.facts ?? []).length > 0);

const sql = (text) => {
  const run = spawnSync("podman", ["exec", "-i", args.container, "psql", "-U", "postgres", "-d", args.db, "-At", "-F", "\t", "-f", "-"], { input: text, encoding: "utf8" });
  if (run.status !== 0) throw new Error(run.stderr);
  return run.stdout.trim().split("\n").filter(Boolean).map((line) => line.split("\t"));
};

let rows = [];
for (const item of cases) {
  const response = await fetch(embed + "/embeddings", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ model: "text-embedding-bge-m3", input: item.query }) });
  const vector = (await response.json()).data[0].embedding;
  // Chunk text has newlines: transport it hex-encoded, one chunk per row.
  const top = sql(`SELECT s."Title", coalesce(c."HeadingTitle",'-'), encode(convert_to(c."Text",'UTF8'),'hex') FROM "KnowledgeChunk" c
    JOIN "SearchDocument" s ON s."PageId"=c."PageId" JOIN "IndexGeneration" g ON g."Id"=c."GenerationId" AND g."State"='Active'
    ORDER BY c."Embedding" <=> '[${vector.join(",")}]'::vector LIMIT 5`)
    .map(([title, heading, text]) => ({ title, heading, text: Buffer.from(text, "hex").toString("utf8") }));
  // A question over several pages (expectAll) is answered by the chunks together: the first rank where the facts are complete.
  const at = item.expectAll ? top.findIndex((_, i) => item.facts.every((fact) => top.slice(0, i + 1).some((chunk) => mentions(chunk.text, fact))))
    : top.findIndex((chunk) => item.facts.every((fact) => mentions(chunk.text, fact)));
  rows.push({ id: item.id, rank: at < 0 ? null : at + 1, top: top.map((chunk) => `${chunk.title} › ${chunk.heading}`) });
  if (args.dump) console.log(`${item.id}: ${at < 0 ? "no chunk with all facts in top 5" : "rank " + (at + 1)}\n  ` + rows.at(-1).top.join("\n  "));
}
const share = (limit) => `${rows.filter((row) => row.rank !== null && row.rank <= limit).length}/${rows.length}`;
for (const row of rows) console.log(`${row.id.padEnd(18)} ${row.rank ?? "-"}`);
console.log(`chunk with all facts: top1 ${share(1)}, top3 ${share(3)}, top5 ${share(5)}`);
