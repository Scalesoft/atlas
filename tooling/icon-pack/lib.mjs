// The rules of the icon pack, free of files and git so that node:test can pin them. generate.mjs applies them.

export const NAME = /^[a-z0-9]+(-[a-z0-9]+)*$/;
export const NAME_MAX = 40;
export const LABEL_MAX = 36;
const WORD_MAX = 24;

/** The Czech categories, in the order of the pills. */
export const CATEGORIES = [
  { id: "text", label: "Text a soubory" },
  { id: "people", label: "Lidé a účty" },
  { id: "communication", label: "Komunikace" },
  { id: "development", label: "Vývoj a data" },
  { id: "devices", label: "Zařízení a sítě" },
  { id: "security", label: "Bezpečnost" },
  { id: "time", label: "Čas a kalendář" },
  { id: "finance", label: "Peníze a obchod" },
  { id: "places", label: "Místa a doprava" },
  { id: "media", label: "Média" },
  { id: "design", label: "Design a rozvržení" },
  { id: "nature", label: "Příroda a věda" },
  { id: "home", label: "Domov a volný čas" },
  { id: "arrows", label: "Šipky a kurzory" },
];

/** Lucide category → Czech category. An icon takes the first row, in this order, whose Lucide category it has. */
export const FOLD = [
  ["security", "security"],
  ["finance", "finance"],
  ["shopping", "finance"],
  ["time", "time"],
  ["mail", "communication"],
  ["medical", "nature"],
  ["science", "nature"],
  ["animals", "nature"],
  ["weather", "nature"],
  ["seasons", "nature"],
  ["sustainability", "nature"],
  ["nature", "nature"],
  ["food-beverage", "home"],
  ["transportation", "places"],
  ["travel", "places"],
  ["buildings", "places"],
  ["sports", "home"],
  ["photography", "media"],
  ["charts", "development"],
  ["math", "development"],
  ["development", "development"],
  ["connectivity", "devices"],
  ["devices", "devices"],
  ["communication", "communication"],
  ["notifications", "communication"],
  ["people", "people"],
  ["accessibility", "people"],
  ["emoji", "people"],
  ["navigation", "places"],
  ["files", "text"],
  ["text", "text"],
  ["home", "home"],
  ["tools", "home"],
  ["multimedia", "media"],
  ["social", "communication"],
  ["account", "people"],
  ["layout", "design"],
  ["design", "design"],
  ["shapes", "design"],
  ["cursors", "arrows"],
  ["arrows", "arrows"],
  ["gaming", "home"],
];

const ELEMENT = /<(path|circle|rect|line|ellipse|polyline|polygon)((?:\s+[a-z0-9-]+="[^"]*")*)\s*\/>/g;
const ATTRIBUTE = /([a-z0-9-]+)="([^"]*)"/g;
const GEOMETRY = new Set(["d", "cx", "cy", "r", "rx", "ry", "x", "y", "x1", "y1", "x2", "y2", "width", "height", "points"]);
const VALUE = /^[0-9a-zA-Z .,+-]+$/;

/** Rebuilt from an allow-list: the web client inserts this text as HTML, so anything else stops the generator. */
export function innerMarkup(svg, name) {
  const root = /^\s*<svg\b([^>]*)>([\s\S]*)<\/svg>\s*$/.exec(svg);
  if (!root) throw new Error(`${name}: not a single <svg> element`);
  if (!/\sviewBox="0 0 24 24"/.test(root[1])) throw new Error(`${name}: viewBox is not 0 0 24 24`);
  let markup = "";
  const rest = root[2].replace(ELEMENT, (_, tag, attributes) => {
    markup += `<${tag}`;
    for (const [, key, value] of attributes.matchAll(ATTRIBUTE)) {
      const allowed = key === "fill" ? value === "currentColor" : GEOMETRY.has(key) && VALUE.test(value);
      if (!allowed) throw new Error(`${name}: attribute ${key}="${value}" is not allowed`);
      markup += ` ${key}="${value}"`;
    }
    markup += "/>";
    return "";
  });
  if (rest.trim()) throw new Error(`${name}: unsupported markup: ${rest.trim().slice(0, 80)}`);
  if (!markup) throw new Error(`${name}: no shapes`);
  return markup;
}

export function geometryKey(markup) {
  return [...markup.matchAll(ELEMENT)]
    .map(([, tag, attributes]) => `${tag} ${[...attributes.matchAll(ATTRIBUTE)].map(([, key, value]) => `${key}=${value}`).sort().join(" ")}`)
    .sort().join("|");
}

/** `lucide`: Map name → { markup, aliases: string[] }; `core`: registry name → markup; `aliases`: atlasIconNames. */
export function selectPack({ lucide, core, catalog, aliases, legacy }) {
  const drawnByCore = new Map();
  for (const [name, markup] of Object.entries(core)) drawnByCore.set(geometryKey(markup), name);
  const offered = new Set(catalog), old = new Set(legacy);
  const pack = [], excluded = {};
  for (const name of [...lucide.keys()].sort()) {
    const icon = lucide.get(name);
    const twin = icon.aliases.find((alias) => alias !== name && alias in core) ?? drawnByCore.get(geometryKey(icon.markup));
    if (offered.has(name)) excluded[name] = "catalog";
    else if (old.has(name)) excluded[name] = "legacy";
    else if (name in aliases && aliases[name] !== name) excluded[name] = `alias:${aliases[name]}`;
    else if (twin && twin !== name) excluded[name] = `core:${twin}`;
    else pack.push(name);
  }
  return { pack, excluded };
}

/** The Czech category of an icon. An unknown Lucide category stops the generator: a new one is folded on purpose. */
export function foldCategory(categories, name) {
  const unknown = categories.find((category) => !FOLD.some(([source]) => source === category));
  if (unknown !== undefined) throw new Error(`${name}: unknown Lucide category ${unknown}`);
  const row = FOLD.find(([source]) => categories.includes(source));
  if (!row) throw new Error(`${name}: no Lucide category`);
  return row[1];
}

/** One space-separated string for the search: the Czech words, then Lucide's tags, then the names; every word once. */
export function searchWords(czech, tags, names) {
  return [...new Set([...czech, ...tags, ...names].flatMap((text) => text.split(/\s+/)).filter(Boolean))].join(" ");
}

const withoutDiacriticsAndCase = (text) => text.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase();

/**
 * What stops the generator in labels.cs.json. `labels`: name → { label, words }; `names`: the pack;
 * `taken`: id → label of the icons offered outside the pack, whose labels the pack may not repeat.
 */
export function labelProblems(labels, names, taken) {
  const problems = [];
  const pack = new Set(names);
  const owners = new Map(Object.entries(taken).map(([id, label]) => [withoutDiacriticsAndCase(label), id]));
  for (const name of [...new Set([...names, ...Object.keys(labels)])].sort()) {
    const say = (problem) => problems.push(`${name}: ${problem}`);
    if (!pack.has(name)) { say("stale entry, the pack has no such icon"); continue; }
    if (!Object.hasOwn(labels, name)) { say("no label"); continue; }
    const { label, words } = labels[name] ?? {};
    if (typeof label !== "string" || !Array.isArray(words) || !words.every((word) => typeof word === "string")) {
      say('the entry is not {"label": "…", "words": ["…", "…"]}');
      continue;
    }
    if (!label || label !== label.trim().replace(/\s+/g, " ")) say("the label is empty or is not trimmed to single spaces");
    if (label.length > LABEL_MAX) say(`the label is longer than ${LABEL_MAX} characters`);
    if (!/^[\p{Lu}\p{Nd}]/u.test(label)) say("the label does not start with a capital letter or a digit");
    if (/[.,;:!?…]$/.test(label)) say("the label ends with punctuation");
    const key = withoutDiacriticsAndCase(label);
    if (owners.has(key)) say(`the label "${label}" is also the label of ${owners.get(key)}`);
    else owners.set(key, name);
    if (words.length < 2 || words.length > 5) say(`${words.length} words instead of 2 to 5`);
    for (const word of words) {
      if (!word || word !== word.trim() || word !== word.toLowerCase() || word.length > WORD_MAX) {
        say(`the word "${word}" is empty, not trimmed, not lower-case or longer than ${WORD_MAX} characters`);
      }
    }
    if (new Set(words).size !== words.length) say("a word repeats");
  }
  return problems;
}

const byText = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

/**
 * A stored name never loses its drawing: an icon the previous pack had and the new one lacks keeps its previous
 * markup and catalog entry, unless the name is valid elsewhere now (the catalog or the legacy list).
 * `previous`, `next`: { markup: name → inner markup, icons: catalog entries }.
 */
export function retainPrevious(previous, next, validElsewhere) {
  const elsewhere = new Set(validElsewhere);
  const kept = Object.keys(previous.markup).filter((name) => !Object.hasOwn(next.markup, name) && !elsewhere.has(name)).sort();
  const markup = { ...next.markup }, icons = [...next.icons];
  for (const name of kept) {
    const icon = previous.icons.find(({ id }) => id === name);
    if (!icon) throw new Error(`${name}: the previous pack draws it and its catalog has no entry`);
    markup[name] = previous.markup[name];
    icons.push(icon);
  }
  return {
    markup: Object.fromEntries(Object.entries(markup).sort(([a], [b]) => byText(a, b))),
    icons: icons.sort((a, b) => byText(a.id, b.id)),
    kept,
  };
}
