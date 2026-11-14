// The mark of Atlas 4.0 „Quiet Quince“, a copy of pear-mark.mjs with its own geometry, a velvet grid of dots instead of the wires
// (`dots`) and the quiet finale (`fade`). Regenerate with
//   node tooling/brand/quince-mark.mjs [out]      (default: src/Atlas.Web/src/assets/brand/quince.svg)
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { wireframeMark } from "./wireframe-mark.mjs";

export const mark = () => wireframeMark({
  id: "quince",
  label: "Atlas 4.0 Quiet Quince",
  desc: "Kdoule z teček ve stylu loga Atlasu: vykreslí se obrys, tečky naskočí odspodu jako sametový povrch, vyroste šťopka se dvěma listy a pak přes kdouli pomalu přejdou tři tiché odlesky; s každým tečky potichu zhasínají, až zbude jen řídká stopa, kterou občas obkreslí světlo.",
  // [t from the top, radius]: between a pear and an apple: a short neck, full shoulders, the widest low, a broad bottom.
  profile: [[0, 0], [0.03, 2.4], [0.08, 3.6], [0.16, 5.6], [0.26, 9], [0.38, 12.8], [0.5, 15.6], [0.62, 17.2], [0.72, 17.6], [0.81, 17], [0.89, 15.2], [0.95, 11.6], [0.985, 6.6], [1, 0]],
  top: 19,
  grow: [{ d: "M32 19.4C31.9 17.2 32.4 15.4 33.6 13.8", origin: [32, 19.4] }],
  // Two leaves, unlike the pear's one: a broad oval leaning left and a small one to the right.
  open: [
    { d: "M32.9 15.6C30.4 10.6 25 8.8 19.6 10.2C22 15.2 27.6 17.4 32.9 15.6Z", rib: "M32.1 15.2C28.6 13.2 24.6 11.8 21 11.2", origin: [32.6, 15.8] },
    { d: "M33.4 15C35.2 12 38.6 11 41.8 11.8C40.2 14.6 36.8 15.8 33.4 15Z", origin: [33.4, 15] },
  ],
  fade: true,
  dots: true,
});

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const out = process.argv[2] ?? fileURLToPath(new URL("../../src/Atlas.Web/src/assets/brand/quince.svg", import.meta.url));
  const svg = mark();
  writeFileSync(out, svg);
  console.log(`${out}: ${svg.length} B`);
}
