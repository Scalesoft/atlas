import type { CalloutVariant } from "./rules";

/**
 * The block conversions of the rule intents as editor JSON (plan §12 H.2): what the proposal panel previews and what
 * Použít writes, so the preview is exactly the result. Pure data: no editor import. Building JSON instead of pasting
 * Markdown keeps the author's text verbatim (no `*` or `_` reinterpreted) and keeps inline components in task items
 * and callouts.
 */
export interface JsonNode {
  type: string;
  attrs?: Record<string, unknown>;
  content?: JsonNode[];
  text?: string;
  marks?: JsonNode[];
}

/** U+FFFC: an inline component inside `MarkdownAssistBlock.text`. */
export const objectReplacement = "￼";

const text = (value: string): JsonNode[] => (value ? [{ type: "text", text: value }] : []);
const paragraph = (content: JsonNode[]): JsonNode => ({ type: "paragraph", ...(content.length ? { content } : {}) });

/** The first line's separator decides the columns (tab, then semicolon, then pipe), as the table rule does. */
export function tableFromText(value: string): JsonNode | null {
  const lines = value.split("\n").map(line => line.trim()).filter(Boolean);
  const separator = ["\t", ";", "|"].find(candidate => lines[0]?.includes(candidate));
  if (!separator || lines.length < 2 || value.includes(objectReplacement)) return null;
  // A Markdown-style pipe row may start and end with the separator.
  const cells = (line: string) => {
    let parts = line.split(separator).map(cell => cell.trim());
    if (separator === "|" && parts.length > 2 && parts[0] === "" && parts.at(-1) === "") parts = parts.slice(1, -1);
    return parts;
  };
  const rows = lines.map(cells).filter(row => !row.every(cell => /^:?-{3,}:?$/.test(cell)));
  const width = Math.max(...rows.map(row => row.length));
  return {
    type: "table",
    content: rows.map((row, index) => ({
      type: "tableRow",
      content: Array.from({ length: width }, (_, column) => ({
        type: index === 0 ? "tableHeader" : "tableCell",
        content: [paragraph(text(row[column] ?? ""))],
      })),
    })),
  };
}

/** Splits a paragraph's inline JSON at hard breaks into lines. */
export function inlineLines(content: readonly JsonNode[] = []): JsonNode[][] {
  const lines: JsonNode[][] = [[]];
  for (const node of content) {
    if (node.type === "hardBreak") lines.push([]);
    else lines.at(-1)!.push(node);
  }
  return lines;
}

const taskMarker = /^\s*(?:-\s*)?(?:\[ \]|TODO(?![\p{L}\p{N}])|úkol:)\s*:?\s*/iu;

/** Removes the leading task marker from the first text node of a line. */
function stripTaskMarker(line: JsonNode[]): JsonNode[] {
  const [first, ...rest] = line;
  if (!first || first.type !== "text" || typeof first.text !== "string") return line;
  const stripped = first.text.replace(taskMarker, "");
  return stripped ? [{ ...first, text: stripped }, ...rest] : rest;
}

/** Lines of a paragraph (hard breaks) as an unchecked task list; the inline components of each line stay. */
export function taskListFromParagraph(content: readonly JsonNode[] = []): JsonNode | null {
  const lines = inlineLines(content).map(stripTaskMarker).filter(line => line.length);
  if (!lines.length) return null;
  return { type: "taskList", content: lines.map(line => ({ type: "taskItem", attrs: { checked: false }, content: [paragraph(line)] })) };
}

/** The paragraph, unchanged, inside an information box of the variant its opening word names. */
export function calloutAround(block: JsonNode, variant: CalloutVariant): JsonNode {
  return { type: "callout", attrs: { variant }, content: [block] };
}

/** A best guess for the code block's language; `null` leaves it plain. */
export function codeLanguage(value: string): string | null {
  const shebang = /^#!\S*?(?:env\s+)?(\w+)/.exec(value)?.[1];
  if (shebang) return ({ sh: "bash", bash: "bash", zsh: "bash", python: "python", python3: "python", node: "javascript", pwsh: "powershell" } as Record<string, string>)[shebang] ?? null;
  if (/^\s*(?:SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER)\s/im.test(value)) return "sql";
  if (/^\s*(?:using\s+[\w.]+;|namespace\s+\w|public\s+(?:sealed\s+)?(?:class|record|interface)\s)/m.test(value)) return "csharp";
  if (/^\s*(?:def\s+\w+\(|from\s+\w+\s+import\s|import\s+\w+\s*$)/m.test(value) && !/[{};]\s*$/m.test(value)) return "python";
  if (/^\s*(?:\$\w+\s*=|Get-\w+|Set-\w+|Write-Host)/m.test(value)) return "powershell";
  if (/=>|\b(?:const|let|function)\s+\w|console\.\w+\(/.test(value)) return "javascript";
  if (/^\s*(?:sudo|echo|apt|cd|ls|export)\s/m.test(value)) return "bash";
  return null;
}

/** The block's text as a code block; `null` when it holds an inline component (it would be lost). */
export function codeBlockFromText(value: string): JsonNode | null {
  if (value.includes(objectReplacement) || !value.trim()) return null;
  return { type: "codeBlock", attrs: { language: codeLanguage(value) }, content: text(value) };
}

/** Mermaid for „A -> B -> C“; text that already is Mermaid stays as written. */
export function diagramSource(value: string): string | null {
  if (value.includes(objectReplacement)) return null;
  const trimmed = value.trim();
  if (/^(?:graph|flowchart)\s+(?:TD|TB|BT|LR|RL)|^(?:sequenceDiagram|classDiagram|stateDiagram(?:-v2)?|erDiagram|gantt)(?![\p{L}\p{N}])/u.test(trimmed)) return trimmed;
  const steps = trimmed.split(/\s*(?:->|→)\s*/u).map(step => step.replace(/["<>{}[\]()|`%]/g, " ").replace(/\s+/g, " ").trim());
  if (steps.length < 2 || steps.some(step => !step)) return null;
  const nodes = steps.map((step, index) => `  s${index + 1}["${step}"]`);
  const edges = steps.slice(1).map((_, index) => `  s${index + 1} --> s${index + 2}`);
  return ["flowchart LR", ...nodes, ...edges].join("\n");
}
