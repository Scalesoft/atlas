// Production build guard and precompression for the Atlas web bundle.
//
// The eager set is everything the browser must download before the first screen: the entry chunks of
// index.html, their static imports and the CSS those import. The build reports its size and fails when it
// grows past the budget or when a heavy library leaks into it.
// The budget guards against regressions, as the editor's do (src/Atlas.Markdown/tests/package/bundle-budget.test.mjs):
// the size measured when it was set plus a fixed 5 % margin. Growing past it is a deliberate decision: cut what grew or
// move it behind `import()`; otherwise measure again and write the new size here.
//
// Every compressible file is also written as .br (quality 11) and .gz (level 9); the server serves those
// variants instead of compressing on each request.
import { createHash } from "node:crypto";
import { mkdir, readFile, readdir, stat, writeFile } from "node:fs/promises";
import { dirname, extname, join, relative, resolve } from "node:path";
import { brotliCompress, constants, gzip } from "node:zlib";
import { promisify } from "node:util";

const brotli = promisify(brotliCompress);
const gz = promisify(gzip);

/** Packages that must stay behind a dynamic import. Matched against module ids of the eager set. */
export const lazyOnlyPackages = [
  "@tiptap/",
  "prosemirror-",
  "yjs",
  "y-protocols",
  "lib0",
  "markdown-it",
  "yaml",
  "mermaid",
  "echarts",
  "katex",
  "highlight.js",
  "lowlight",
  "monaco-editor",
  "@scalar/",
  "@microsoft/signalr",
];

/**
 * Atlas's own modules that must stay behind a dynamic import, by the name a leak is reported under: the drawings (in parts,
 * vite.config.ts iconPackParts) and the catalog of the icon pack (src/iconPack.ts); the assistant's panel and the editor's assist
 * module, which load on intent (P17, docs/interaction-rules.md). Found in the module ids of the eager set.
 */
export const lazyOnlyModules = {
  "icon-pack": ["virtual:icon-pack/part/", "/src/assets/icon-pack/markup.json", "/src/assets/icon-pack/catalog.json"],
  assistant: ["/src/components/assistant/AssistantPanel.vue", "/Atlas.Markdown/dist/assist.js", "/Atlas.Markdown/src/assist/"],
};

const compressible = new Set([".js", ".mjs", ".css", ".svg", ".json", ".ttf", ".otf", ".wasm", ".txt", ".xml", ".map"]);

function packageOf(id) {
  const normalized = id.replaceAll("\\", "/");
  const at = normalized.lastIndexOf("/node_modules/");
  if (at < 0) return undefined;
  const rest = normalized.slice(at + "/node_modules/".length).split("/");
  return rest[0].startsWith("@") ? `${rest[0]}/${rest[1]}` : rest[0];
}

function isLazyOnly(pkg) {
  return lazyOnlyPackages.some((pattern) => pattern.endsWith("/") || pattern.endsWith("-")
    ? pkg.startsWith(pattern)
    : pkg === pattern);
}

function lazyOnlyModule(id) {
  const path = id.replaceAll("\\", "/").split("?", 1)[0];
  return Object.keys(lazyOnlyModules).find((name) => lazyOnlyModules[name].some((module) => path.includes(module)));
}

async function sizes(buffer) {
  const [br, gzip9] = await Promise.all([
    brotli(buffer, { params: { [constants.BROTLI_PARAM_QUALITY]: 11, [constants.BROTLI_PARAM_SIZE_HINT]: buffer.length } }),
    gz(buffer, { level: 9 }),
  ]);
  return { raw: buffer.length, gzip: gzip9.length, brotli: br.length, br, gzip9 };
}

async function* files(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) yield* files(path);
    else yield path;
  }
}

async function limit(tasks, concurrency) {
  const queue = [...tasks];
  await Promise.all(Array.from({ length: concurrency }, async () => {
    for (let task = queue.shift(); task; task = queue.shift()) await task();
  }));
}

/** The viewBox of a space drawing as Vite writes it into an inlined data URL (spaces kept or percent-encoded). */
const inlinedSpaceCover = /0(?: |%20)0(?: |%20)640(?: |%20)220/;
const digest = (data) => createHash("sha256").update(data).digest("hex");

/**
 * The built-in space drawings (src/assets/space-covers) must each be emitted as an asset file with their exact bytes,
 * and no chunk may carry one as a data URL (src/spaceCoverDrawings.ts). Returns what is wrong; empty when fine.
 * @param {Record<string, { type: string, fileName: string, code?: string, source?: string | Uint8Array }>} bundle
 * @param {Map<string, Uint8Array>} drawings id → file content
 * @returns {string[]}
 */
export function spaceCoverProblems(bundle, drawings) {
  const outputs = Object.values(bundle);
  const emitted = new Set(outputs.filter((output) => output.type === "asset" && output.fileName.endsWith(".svg")).map((output) => digest(output.source)));
  const missing = [...drawings].filter(([, content]) => !emitted.has(digest(content))).map(([id]) => id);
  const inlined = outputs.filter((output) => output.type === "chunk" && inlinedSpaceCover.test(output.code ?? "")).map((output) => output.fileName);
  return [
    ...(missing.length > 0 ? [`not emitted as files of their own: ${missing.join(", ")}`] : []),
    ...(inlined.length > 0 ? [`inlined into ${inlined.join(", ")}`] : []),
  ];
}

/**
 * @param {{ budgetGzipKiB?: number, reportFile?: string }} [options]
 * @returns {import("vite").Plugin}
 */
export function atlasBundle(options = {}) {
  // Measured on 2026-10-28 (load speed, round 2: the icon pack's part loaders and the early-read adoption): 297.3 KiB gzip, plus the margin.
  // 4.1 (2026-11-07, English tool texts): the shell's Czech texts are message objects now (4.0 shipped 306.3 KiB gzip); the icon catalog
  // and its names left the eager set for the picker. The owner accepted the localization cost (decision 2026-11-07): the dictionaries
  // ride in content-hashed immutable chunks, so the browser caches them and a change invalidates only its own.
  // Measured 314.4 KiB gzip, plus the 3 % margin.
  const budget = options.budgetGzipKiB ?? 314.4 * 1.03;
  let chunks = new Map();
  let root = process.cwd();
  return {
    name: "atlas-bundle",
    apply: "build",
    configResolved(config) {
      root = config.root;
    },
    async writeBundle(outputOptions, bundle) {
      chunks = new Map();
      for (const output of Object.values(bundle)) {
        if (output.type !== "chunk") continue;
        chunks.set(output.fileName, {
          isEntry: output.isEntry && !output.isDynamicEntry && output.facadeModuleId?.replaceAll("\\", "/").endsWith("/index.html"),
          imports: output.imports,
          css: [...(output.viteMetadata?.importedCss ?? [])],
          modules: output.moduleIds ?? Object.keys(output.modules ?? {}),
        });
      }
      const outDir = outputOptions.dir ?? resolve(root, "dist");
      // Not a size budget: an inlined drawing breaks the contract that each is a cacheable file of its own.
      const coverDirectory = resolve(root, "src/assets/space-covers");
      const drawings = new Map();
      for (const name of (await readdir(coverDirectory)).filter((file) => file.endsWith(".svg")).sort()) {
        drawings.set(name.slice(0, -".svg".length), await readFile(join(coverDirectory, name)));
      }
      const coverProblems = spaceCoverProblems(bundle, drawings);
      if (coverProblems.length > 0) this.error(`Space drawings ${coverProblems.join("; ")}. See src/spaceCoverDrawings.ts.`);
      this.info?.(`space drawings: ${drawings.size} files of their own, none inlined`);
      const eager = new Set();
      const visit = (fileName) => {
        if (eager.has(fileName)) return;
        eager.add(fileName);
        const chunk = chunks.get(fileName);
        if (!chunk) return;
        for (const css of chunk.css) eager.add(css);
        for (const imported of chunk.imports) visit(imported);
      };
      for (const [fileName, chunk] of chunks) if (chunk.isEntry) visit(fileName);

      const leaked = new Map();
      for (const fileName of eager) {
        for (const id of chunks.get(fileName)?.modules ?? []) {
          const pkg = packageOf(id);
          const leak = pkg && isLazyOnly(pkg) ? pkg : lazyOnlyModule(id);
          if (leak) leaked.set(leak, [...(leaked.get(leak) ?? []), fileName]);
        }
      }

      const eagerSizes = [];
      for (const fileName of eager) {
        const measured = await sizes(await readFile(join(outDir, fileName)));
        eagerSizes.push({ file: fileName, raw: measured.raw, gzip: measured.gzip, brotli: measured.brotli });
      }
      eagerSizes.sort((a, b) => b.gzip - a.gzip);
      const total = eagerSizes.reduce((sum, file) => ({ raw: sum.raw + file.raw, gzip: sum.gzip + file.gzip, brotli: sum.brotli + file.brotli }), { raw: 0, gzip: 0, brotli: 0 });
      const kib = (bytes) => (bytes / 1024).toFixed(1);

      const report = {
        budgetGzipKiB: budget,
        eager: { files: eagerSizes.length, rawKiB: +kib(total.raw), gzipKiB: +kib(total.gzip), brotliKiB: +kib(total.brotli) },
        leakedLazyPackages: Object.fromEntries([...leaked].map(([pkg, where]) => [pkg, [...new Set(where)]])),
        files: eagerSizes,
      };
      const reportFile = options.reportFile ?? resolve(root, "obj/bundle-report.json");
      await mkdir(dirname(reportFile), { recursive: true });
      await writeFile(reportFile, JSON.stringify(report, null, 2));
      this.info?.(`eager ${eagerSizes.length} files: ${kib(total.gzip)} KiB gzip / ${kib(total.brotli)} KiB brotli (budget ${kib(budget * 1024)} KiB gzip); report ${relative(root, reportFile)}`);

      const problems = [];
      if (leaked.size > 0) problems.push(`lazy-only packages in the eager set: ${[...leaked.keys()].join(", ")}`);
      if (total.gzip > budget * 1024) problems.push(`eager set ${kib(total.gzip)} KiB gzip exceeds the ${kib(budget * 1024)} KiB budget`);
      if (problems.length > 0) this.error(`Bundle budget: ${problems.join("; ")}. See ${reportFile}.`);

      const tasks = [];
      for await (const path of files(outDir)) {
        if (!compressible.has(extname(path))) continue;
        tasks.push(async () => {
          if ((await stat(path)).size < 1024) return;
          const measured = await sizes(await readFile(path));
          if (measured.brotli < measured.raw * 0.9) await writeFile(`${path}.br`, measured.br);
          if (measured.gzip < measured.raw * 0.9) await writeFile(`${path}.gz`, measured.gzip9);
        });
      }
      await limit(tasks, 8);
    },
  };
}
