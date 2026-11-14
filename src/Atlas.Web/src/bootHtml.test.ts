// @vitest-environment node
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { bootHtml, staticImports } from "../scripts/bootHtml.mjs";

const chunk = (fileName: string, imports: string[], extra = {}) => [fileName, { type: "chunk", fileName, name: fileName.replace(/\.js$/, ""), imports, isEntry: false, ...extra }];
const bundle = Object.fromEntries([
  chunk("index.js", ["shared.js"], { isEntry: true }),
  chunk("shared.js", []),
  chunk("DocumentEditor.js", ["shared.js", "registry.js"]),
  chunk("registry.js", ["codec.js"]),
  chunk("codec.js", ["registry.js"]),
  chunk("Lazy.js", []),
]);

describe("boot order of index.html", () => {
  it("lists the route's static imports without the entry's own and without cycles", () => {
    expect(staticImports(bundle, "DocumentEditor.js", new Set(["index.js", "shared.js"]))).toEqual(["DocumentEditor.js", "registry.js", "codec.js"]);
  });

  it("moves stylesheets behind the skeleton and adds low-priority preloads", () => {
    const html = '<head>\n  <link rel="stylesheet" crossorigin href="/a.css">\n</head><body><div id="app"></div></body>';
    const out = (bootHtml().transformIndexHtml as { handler: (html: string, ctx: unknown) => string }).handler(html, { bundle });
    expect(out.indexOf("</head>")).toBeLessThan(out.indexOf("a.css"));
    expect(out).toContain('<link rel="modulepreload" crossorigin fetchpriority="low" href="/codec.js">');
    expect(out).not.toContain('href="/shared.js"');
  });

  it("declares the cascade layer order first in <head>, before any lazy chunk stylesheet can declare layers", () => {
    const html = readFileSync("index.html", "utf8");
    expect(html.match(/<style>([^<]*)/)?.[1].startsWith("@layer theme,base,components,utilities;")).toBe(true);
  });
});
