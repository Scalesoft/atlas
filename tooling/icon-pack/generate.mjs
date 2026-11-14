// The icon pack behind "Další ikony": one pinned Lucide release becomes the committed files the web and the server read.
// node tooling/icon-pack/generate.mjs [--todo | --merge | --sample] [--source <lucide checkout>]; see README.md.
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { CATEGORIES, FOLD, NAME, NAME_MAX, foldCategory, innerMarkup, labelProblems, retainPrevious, searchWords, selectPack } from "./lib.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, "../..");
const web = join(root, "src/Atlas.Web");
const cache = join(here, ".cache");
const files = {
  labels: join(here, "labels.cs.json"),
  markup: join(web, "src/assets/icon-pack/markup.json"),
  catalog: join(web, "src/assets/icon-pack/catalog.json"),
  meta: join(web, "src/assets/icon-pack/meta.json"),
  names: join(root, "src/Atlas.Server/Platform/Validation/icon-pack-names.txt"),
};
/** Stored and accepted, offered by no picker: the retired "Novinky" and the icon a Wiki.js import gives its space. */
const LEGACY = ["sparkles", "archive"];
const TODO_SIZE = 150, SAMPLE_STEP = 30;

const git = (cwd, ...args) => execFileSync("git", args, { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], windowsHide: true }).trim();
const readJson = (file, missing) => (missing === undefined || existsSync(file) ? JSON.parse(readFileSync(file, "utf8")) : missing);
/** One entry per line and LF, so that a diff shows the icons that changed. */
const lines = (open, entries, close) => `${open}\n${entries.join(",\n")}\n${close}\n`;
/** JSON on one line with a space after each colon and comma: the form people correct labels.cs.json in. */
const spaced = (value) =>
  Array.isArray(value) ? `[${value.map(spaced).join(", ")}]`
  : value && typeof value === "object" ? `{${Object.entries(value).map(([key, item]) => `${JSON.stringify(key)}: ${spaced(item)}`).join(", ")}}`
  : JSON.stringify(value);

/** The pinned Lucide sources: a sparse clone in the cache, or the checkout given by --source. Its files are only read. */
function checkout(source, given) {
  const dir = given ? resolve(given) : join(cache, `lucide-${source.version}`);
  if (!existsSync(dir)) {
    if (given) throw new Error(`${dir} does not exist`);
    mkdirSync(cache, { recursive: true });
    git(cache, "clone", "--depth", "1", "--branch", source.version, "--filter=blob:none", "--sparse", "--", source.repository, dir);
    git(dir, "sparse-checkout", "set", "icons", "categories");
  }
  if (git(dir, "rev-parse", "HEAD") !== source.commit) throw new Error(`${dir} is not at the pinned commit ${source.commit}`);
  if (git(dir, "status", "--porcelain", "--", "icons", "categories")) throw new Error(`${dir} has local changes in icons or categories`);
  return dir;
}

/** name → { markup, aliases, tags, categories } of every icon of the checkout. */
function readLucide(dir) {
  const folded = new Set(FOLD.map(([category]) => category));
  for (const file of readdirSync(join(dir, "categories"))) {
    const category = file.replace(/\.json$/, "");
    if (category !== file && !folded.has(category)) throw new Error(`unknown Lucide category ${category}: fold it in lib.mjs`);
  }
  const lucide = new Map();
  for (const entry of readdirSync(join(dir, "icons"), { withFileTypes: true })) {
    if (!entry.isFile() || !entry.name.endsWith(".svg")) continue;
    const name = entry.name.slice(0, -".svg".length);
    const meta = JSON.parse(readFileSync(join(dir, "icons", `${name}.json`), "utf8"));
    lucide.set(name, {
      markup: innerMarkup(readFileSync(join(dir, "icons", entry.name), "utf8"), name),
      aliases: (meta.aliases ?? []).map((alias) => alias.name ?? alias),
      tags: meta.tags ?? [],
      categories: meta.categories ?? [],
    });
  }
  return lucide;
}

/** What Atlas draws and offers already: the core registry, the offered icons with their labels, the wiki-local names. */
async function readAtlas() {
  const editor = join(web, "node_modules/@atlas/markdown");
  const icons = JSON.parse(readFileSync(join(editor, "package.json"), "utf8")).exports["./icons"].import;
  const { iconMarkup: core } = await import(pathToFileURL(join(editor, icons)).href);
  // The ids are in the catalog, the Czech names in the dictionary it reads (its `icons` block).
  const catalog = readFileSync(join(web, "src/components/settings/appearanceIcons.ts"), "utf8");
  const block = /export const appearanceIcons\b[\s\S]*?\n\] satisfies/.exec(catalog)?.[0] ?? "";
  const dictionary = /icons: \{([\s\S]*?)\n  \} as/.exec(readFileSync(join(web, "src/i18n/appearance-icons.ts"), "utf8"))?.[1] ?? "";
  const labels = Object.fromEntries([...dictionary.matchAll(/^\s*"([a-z0-9-]+)": "([^"]+)",/gm)].map(([, id, label]) => [id, label]));
  const offered = Object.fromEntries([...block.matchAll(/\{ id: "([a-z0-9-]+)", group:/g)].map(([, id]) => [id, labels[id] ?? ""]));
  if (Object.keys(offered).length < 80) throw new Error("appearanceIcons.ts: fewer than 80 offered icons were read");
  const names = readFileSync(join(web, "src/components/atlasIconNames.ts"), "utf8");
  const aliases = Object.fromEntries([...names.matchAll(/^\s*"?([a-z0-9-]+)"?:\s*"([a-z0-9-]+)",/gm)].map(([, key, value]) => [key, value]));
  if (Object.keys(aliases).length < 40) throw new Error("atlasIconNames.ts: fewer than 40 names were read");
  return { core, offered, aliases };
}

try {
  const args = process.argv.slice(2);
  const at = args.indexOf("--source");
  const given = at < 0 ? undefined : args.splice(at, 2)[1];
  const mode = args[0] ?? "";
  if (args.length > 1 || !["", "--todo", "--merge", "--sample"].includes(mode) || (at >= 0 && !given)) {
    throw new Error("usage: generate.mjs [--todo | --merge | --sample] [--source <lucide checkout>]");
  }

  const source = readJson(join(here, "source.json"));
  if (!/^\d+\.\d+\.\d+$/.test(source.version) || !/^[0-9a-f]{40}$/.test(source.commit)) {
    throw new Error("source.json: expected a version x.y.z and a full commit");
  }
  const lucide = readLucide(checkout(source, given));
  const atlas = await readAtlas();
  const validElsewhere = [...Object.keys(atlas.offered), ...LEGACY];
  const { pack, excluded } = selectPack({ lucide, core: atlas.core, catalog: Object.keys(atlas.offered), aliases: atlas.aliases, legacy: LEGACY });

  const previous = { markup: readJson(files.markup, {}), icons: readJson(files.catalog, { icons: [] }).icons };
  const drawn = Object.fromEntries(pack.map((name) => [name, lucide.get(name).markup]));
  const retain = (icons) => retainPrevious(previous, { markup: drawn, icons }, validElsewhere);
  // The names are needed before any label is read: the labels are validated against them.
  const names = Object.keys(retain([]).markup).sort();
  for (const name of names) {
    if (name.length > NAME_MAX || !NAME.test(name)) throw new Error(`${name}: not a lower-case kebab-case name of at most ${NAME_MAX} characters`);
  }

  let labels = readJson(files.labels, {});
  if (mode === "--todo") {
    const todo = pack.filter((name) => !Object.hasOwn(labels, name)).map((name) => ({ name, tags: lucide.get(name).tags, categories: lucide.get(name).categories }));
    const count = Math.ceil(todo.length / TODO_SIZE);
    mkdirSync(cache, { recursive: true });
    // A file of an earlier round would be translated again and its labels would replace corrected ones.
    for (const file of readdirSync(cache)) if (/^todo-\d+\.json$/.test(file)) rmSync(join(cache, file));
    for (let index = 0; index < count; index++) {
      const part = todo.slice(index * TODO_SIZE, (index + 1) * TODO_SIZE);
      writeFileSync(join(cache, `todo-${String(index + 1).padStart(2, "0")}.json`), lines("[", part.map((icon) => JSON.stringify(icon)), "]"));
    }
    console.log(`${todo.length} icons without a label -> ${count} files`);
  } else if (mode === "--merge") {
    const parts = existsSync(cache) ? readdirSync(cache).filter((file) => /^labels-part-\d+\.json$/.test(file)).sort() : [];
    for (const file of parts) {
      const part = JSON.parse(readFileSync(join(cache, file), "utf8"));
      if (!part || typeof part !== "object" || Array.isArray(part)) throw new Error(`${file}: not an object of name → { label, words }`);
      labels = { ...labels, ...part };
    }
    writeFileSync(files.labels, lines("{", Object.keys(labels).sort().map((name) => `${JSON.stringify(name)}: ${spaced(labels[name])}`), "}"));
    // A part is folded once: left in place, the next --merge would put it over the corrections made in labels.cs.json.
    for (const file of parts) renameSync(join(cache, file), join(cache, file.replace(/\.json$/, ".merged.json")));
    const problems = labelProblems(labels, names, atlas.offered);
    for (const problem of problems) console.log(problem);
    console.log(`${problems.length} problems`);
    if (problems.length) process.exitCode = 1;
  } else if (mode === "--sample") {
    for (const name of names.filter((_, index) => index % SAMPLE_STEP === 0)) {
      console.log(`${name}: ${labels[name]?.label ?? "no label"} (${[].concat(labels[name]?.words ?? []).join(", ")})`);
    }
  } else {
    const problems = labelProblems(labels, names, atlas.offered);
    for (const problem of problems) console.error(problem);
    if (problems.length) throw new Error(`${problems.length} problems in labels.cs.json, nothing was written`);

    const { markup, icons, kept } = retain(pack.map((name) => {
      const icon = lucide.get(name);
      return { id: name, label: labels[name].label, category: foldCategory(icon.categories, name), words: searchWords(labels[name].words, icon.tags, [...icon.aliases, name]) };
    }));
    // What is carried over from the committed file goes through the allow-list like a fresh drawing.
    for (const name of kept) {
      if (innerMarkup(`<svg viewBox="0 0 24 24">${markup[name]}</svg>`, name) !== markup[name]) throw new Error(`${name}: the kept drawing is not in the generated form`);
    }
    const core = Object.fromEntries(Object.keys(atlas.offered).sort().map((id) => {
      const icon = lucide.get(id) ?? [...lucide.values()].find(({ aliases }) => aliases.includes(id));
      if (!icon) throw new Error(`${id}: no Lucide icon of that name or former name gives its category`);
      return [id, foldCategory(icon.categories, id)];
    }));

    for (const file of [files.markup, files.names]) mkdirSync(dirname(file), { recursive: true });
    writeFileSync(files.markup, lines("{", names.map((name) => `${JSON.stringify(name)}: ${JSON.stringify(markup[name])}`), "}"));
    writeFileSync(files.catalog, lines("{", [
      `"lucide": ${JSON.stringify(source.version)}`,
      `"categories": ${JSON.stringify(CATEGORIES)}`,
      `"core": ${JSON.stringify(core)}`,
      `"icons": [\n${icons.map((icon) => JSON.stringify(icon)).join(",\n")}\n]`,
    ], "}"));
    writeFileSync(files.meta, `${JSON.stringify({ lucide: source.version, count: names.length })}\n`);
    writeFileSync(files.names, `${names.join("\n")}\n`);

    const left = (reason) => Object.values(excluded).filter((value) => value.split(":")[0] === reason).length;
    console.log(`${names.length} icons from Lucide ${source.version} (${lucide.size} upstream; left out: ${left("catalog")} catalog, ${left("legacy")} legacy, ${left("alias")} alias, ${left("core")} core; kept from the previous pack: ${kept.length})`);
  }
} catch (error) {
  console.error(`icon-pack: ${error.message}`);
  process.exitCode = 1;
}
