import type { Component } from "vue";

/**
 * The live demos a release's splash shows in its highlight cards (`demos:` in the notes, ReleaseSplash), by name: each loads its
 * component (`components/release/demos/<name>.vue`) on demand, as a chunk of its own, only when a splash shows it. A demo is a small,
 * self-running illustration with local sample data (`example.test`), no backend calls; under reduced motion or `[data-still]` it
 * stands as its finished picture.
 */
export const releaseDemos: Record<string, () => Promise<Component>> = Object.fromEntries(
  Object.entries(import.meta.glob<Component>("../components/release/demos/*.vue", { import: "default" }))
    .map(([path, load]) => [path.split("/").at(-1)!.replace(/\.vue$/, ""), load]),
);
