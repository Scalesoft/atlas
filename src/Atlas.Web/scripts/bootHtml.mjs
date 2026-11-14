// Boot order of the built index.html (Atlas 3.6-G, bundle review 2026-11-03):
//  - the stylesheets move behind the skeleton in <body>: a stylesheet in <head> blocks the first paint until its 290 KB
//    are in, a stylesheet after the skeleton blocks only what follows it, so the skeleton paints at once;
//  - the static imports of the page route (DocumentEditor and everything it pulls in) are preloaded at low priority, after
//    the entry's own chunks. They are a bounded list from the build, not a hand-kept one; unbounded and at normal priority
//    they delayed the first paint by 100 ms because they competed with the entry chunk and the CSS.
const stylesheet = /^\s*<link rel="stylesheet"[^>]*>\n?/gm;

/** The files a chunk needs when it is loaded: its static imports, transitively, minus those already in `skip`. */
export function staticImports(bundle, entryName, skip) {
  const found = new Set();
  const visit = (name) => {
    if (found.has(name) || skip.has(name) || bundle[name]?.type !== "chunk") return;
    found.add(name);
    bundle[name].imports.forEach(visit);
  };
  visit(entryName);
  return [...found];
}

/** @returns {import("vite").Plugin} */
export function bootHtml(options = { route: "DocumentEditor" }) {
  return {
    name: "atlas-boot-html",
    apply: "build",
    transformIndexHtml: {
      order: "post",
      handler(html, { bundle }) {
        const styles = html.match(stylesheet) ?? [];
        let out = html.replace(stylesheet, "");
        const chunks = Object.values(bundle ?? {}).filter((output) => output.type === "chunk");
        const route = chunks.find((chunk) => chunk.name === options.route);
        const eager = new Set(chunks.filter((chunk) => chunk.isEntry).flatMap((chunk) => [chunk.fileName, ...chunk.imports]));
        const preload = route ? staticImports(bundle, route.fileName, eager) : [];
        const links = preload.map((name) => `<link rel="modulepreload" crossorigin fetchpriority="low" href="/${name}">`);
        return out.replace("</body>", `${styles.join("")}${links.join("")}</body>`);
      },
    },
  };
}
