// The mark of Atlas 3.5 „Performant Pear“: the first use of the wireframe mark (wireframe-mark.mjs). Regenerate the asset with
//   node tooling/brand/pear-mark.mjs [out]      (default: src/Atlas.Web/src/assets/brand/pear.svg)
// The next minor or major release copies this file, gives its own geometry and writes assets/brand/<mark>.svg.
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { wireframeMark } from "./wireframe-mark.mjs";

export const mark = () => wireframeMark({
  id: "pear",
  label: "Atlas 3.5 Performant Pear",
  desc: "Drátěná hruška ve stylu loga Atlasu: vykreslí se obrys, rozvine se drátěný model, vyroste šťopka s listem, po drátech přebíhá stále rychlejší světlo a pak se hruška jemně houpe.",
  // [t from the top, radius]: a narrow neck, the widest at 0.8, a flat bottom.
  profile: [[0, 0], [0.03, 2.6], [0.1, 3.9], [0.22, 4.9], [0.34, 6.6], [0.46, 9.8], [0.58, 13.4], [0.7, 15.9], [0.8, 16.7], [0.88, 15.8], [0.93, 13.8], [0.97, 10.2], [0.99, 6], [1, 0]],
  grow: [{ d: "M32 17.4C32.1 13.2 33.6 9.6 36.4 6.6", origin: [32, 17.4] }],
  // The leaf echoes a Bloom page: a lens from the stem, with its rib.
  open: [{ d: "M34.6 10.8C36.8 6.6 41.6 5.2 46.4 6.4C44.4 10.8 39.4 12.6 34.6 10.8Z", rib: "M35.4 10.4C38.6 8.8 42 7.8 45.2 7.2", origin: [35, 11] }],
});

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const out = process.argv[2] ?? fileURLToPath(new URL("../../src/Atlas.Web/src/assets/brand/pear.svg", import.meta.url));
  const svg = mark();
  writeFileSync(out, svg);
  console.log(`${out}: ${svg.length} B`);
}
