// Markdown table of agent-relevance.mjs reports (per question: answer ok / cited ok / expected page among sources, seconds) and totals.
//   node tests/ai-eval/projector/summarize.mjs label=report.json [label=report.json ...]
import { readFileSync } from "node:fs";
import { scoreAnswer } from "./score.mjs";

// The answer is scored again from the stored text with the current cases.json, so facts can be tuned without asking the model again.
const cases = new Map(JSON.parse(readFileSync(new URL("./cases.json", import.meta.url), "utf8")).cases.map((item) => [item.id, item]));
const rescored = (row) => row.state === "error" || row.answer?.startsWith("ERROR") ? { ...row, answerOk: undefined }
  : { ...row, answerOk: scoreAnswer(cases.get(row.id), { answer: row.answer ?? "", cited: row.cited ?? [], sources: row.cited ?? [], toolCalls: row.toolCalls }).answerOk };

const runs = process.argv.slice(2).map((arg) => {
  const at = arg.indexOf("=");
  return { label: arg.slice(0, at), rows: ((report) => report.agent ?? report.results)(JSON.parse(readFileSync(arg.slice(at + 1), "utf8"))).map(rescored) };
});
const mark = (value) => value === null || value === undefined ? "-" : value ? "ok" : "NO";
const ids = [...new Set(runs.flatMap((run) => run.rows.map((row) => row.id)))];
console.log(`| Otázka | ${runs.map((run) => run.label).join(" | ")} |`);
console.log(`|---|${runs.map(() => "---").join("|")}|`);
for (const id of ids) {
  const cells = runs.map((run) => {
    const row = run.rows.find((item) => item.id === id);
    return row ? `${mark(row.answerOk)} / ${mark(row.citedOk)} / ${mark(row.inSources)} / ${(row.ms / 1000).toFixed(0)} s` : "";
  });
  console.log(`| ${id} | ${cells.join(" | ")} |`);
}
const pct = (rows, pick) => `${Math.round((100 * rows.filter(pick).length) / rows.length)} %`;
const cited = (rows) => rows.filter((row) => row.citedOk !== null && row.citedOk !== undefined);
const median = (values) => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];
console.log(`| **odpověď ok** | ${runs.map((run) => `${run.rows.filter((row) => row.answerOk).length}/${run.rows.length}`).join(" | ")} |`);
console.log(`| **citace ok** | ${runs.map((run) => `${cited(run.rows).filter((row) => row.citedOk).length}/${cited(run.rows).length}`).join(" | ")} |`);
console.log(`| **zdroje ok** | ${runs.map((run) => `${cited(run.rows).filter((row) => row.inSources).length}/${cited(run.rows).length}`).join(" | ")} |`);
console.log(`| **medián / max** | ${runs.map((run) => `${(median(run.rows.map((row) => row.ms)) / 1000).toFixed(0)} / ${(Math.max(...run.rows.map((row) => row.ms)) / 1000).toFixed(0)} s`).join(" | ")} |`);
