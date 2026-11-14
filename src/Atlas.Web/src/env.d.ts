/** The @atlas/markdown version bundled into this build (vite.config.ts). */
declare const __MARKDOWN_EDITOR_VERSION__: string;

/** One loader per part of the icon pack's drawings, name → inner SVG markup (vite.config.ts iconPackParts). */
declare module "virtual:icon-pack/parts" {
  const parts: ReadonlyArray<() => Promise<{ default: Readonly<Record<string, string>> }>>;
  export default parts;
}
