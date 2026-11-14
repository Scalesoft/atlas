import type { Plugin } from "vite";

export declare const lazyOnlyPackages: string[];
export declare const lazyOnlyModules: Record<string, string[]>;
export declare function atlasBundle(options?: { budgetGzipKiB?: number; reportFile?: string }): Plugin;
export declare function spaceCoverProblems(
  bundle: Record<string, { type: string; fileName: string; code?: string; source?: string | Uint8Array }>,
  drawings: Map<string, Uint8Array>,
): string[];
