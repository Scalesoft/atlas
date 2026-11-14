import type { Plugin } from "vite";

export declare function staticImports(
  bundle: Record<string, { type: string; imports?: string[] }>,
  entryName: string,
  skip: Set<string>,
): string[];
export declare function bootHtml(options?: { route: string }): Plugin;
