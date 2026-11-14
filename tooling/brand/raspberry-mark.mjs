// The mark of Atlas 4.1 „Radiant Raspberry“, a copy of pear-mark.mjs with its own geometry: a cluster of round drupelets
// (`drupelets`) with the radiant finale (`radiant`). Regenerate with
//   node tooling/brand/raspberry-mark.mjs [out]      (default: src/Atlas.Web/src/assets/brand/raspberry.svg)
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { wireframeMark } from "./wireframe-mark.mjs";

export const mark = () => wireframeMark({
  id: "raspberry",
  label: "Atlas 4.1 Radiant Raspberry",
  desc: "Malina z kuliček ve stylu loga Atlasu: vykreslí se obrys, kuličky naskočí odspodu a nahoře vyroste šťopka s drobnými kališními lístky; pak středem malinu proběhne záře, pokaždé silnější: kuličky se rozsvítí od středu ven a kolem zablikají krátké paprsky, až se celá malina rozplyne ve světle a nezbyde nic.",
  // [t from the top, radius]: a round dome, nearly as wide low as high up, with a fully round blunt bottom.
  profile: [[0, 0], [0.02, 5], [0.06, 10], [0.12, 13.6], [0.22, 15.6], [0.4, 16], [0.58, 15.2], [0.72, 13.6], [0.84, 11], [0.93, 7.6], [0.98, 4], [1, 0]],
  top: 21,
  bottom: 55,
  grow: [{ d: "M32 21.8C32 20 32.4 18.6 33.4 17.2", origin: [32, 21.8] }],
  // The calyx is only a few small thin sepals peeking over the top edge.
  open: [
    { d: "M32 21.8C30.4 21 28.6 21 27.2 22.2C29 22.2 30.6 22.4 32 21.8Z", origin: [32, 21.8] },
    { d: "M32 21.8C33.6 21 35.4 21 36.8 22.2C35 22.2 33.4 22.4 32 21.8Z", origin: [32, 21.8] },
    { d: "M32 21.6C31 19.8 29.8 19 28.4 18.8C29.2 20.2 30.6 21.2 32 21.6Z", origin: [32, 21.6] },
    { d: "M32 21.6C33 19.8 34.2 19 35.6 18.8C34.8 20.2 33.4 21.2 32 21.6Z", origin: [32, 21.6] },
  ],
  drupelets: true,
  radiant: true,
});

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const out = process.argv[2] ?? fileURLToPath(new URL("../../src/Atlas.Web/src/assets/brand/raspberry.svg", import.meta.url));
  const svg = mark();
  writeFileSync(out, svg);
  console.log(`${out}: ${svg.length} B`);
}
