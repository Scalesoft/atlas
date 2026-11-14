/**
 * The release marks in `assets/brand/<mark>.svg` (tooling/brand/wireframe-mark.mjs), by name: each loads its SVG text on demand, as a
 * chunk of its own. ReleaseMark draws it inline, so its motion starts when it appears (an <img> runs on the page's clock).
 */
export const brandMarks: Record<string, () => Promise<string>> = Object.fromEntries(
  Object.entries(import.meta.glob<string>("../assets/brand/*.svg", { query: "?raw", import: "default" }))
    .map(([path, load]) => [path.split("/").at(-1)!.replace(/\.svg$/, ""), load]),
);
