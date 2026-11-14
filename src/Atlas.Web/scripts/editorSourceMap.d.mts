export declare const webRoot: string;
export declare const editorPackageRoot: string;
export declare const editorSourceExports: Readonly<Record<string, string>>;
export declare const unreleasedEditorExports: Readonly<Record<string, string>>;
export declare const unreleasedEditorFallbacks: Readonly<Record<string, string>>;
export declare function editorFallbackEntries(entries: Readonly<Record<string, string>>): Readonly<Record<string, string>>;
export declare function editorFallbackAliases(entries: Readonly<Record<string, string>>): { find: RegExp; replacement: string }[];
export declare function editorFallbackPaths(entries: Readonly<Record<string, string>>): Record<string, string[]>;
export declare function isEditorSourceMode(env?: Record<string, string | undefined>): boolean;
export declare function editorSourceRoot(env?: Record<string, string | undefined>): string;
export declare function assertEditorSource(editor: string): void;
export declare function exportedSpecifiers(manifest: { exports?: Record<string, unknown> }): string[];
export declare function editorSourceEntries(editor: string): Readonly<Record<string, string>>;
export declare function editorSourceAliases(editor: string): { find: RegExp; replacement: string }[];
export declare function editorSourcePaths(editor: string): Record<string, string[]>;
export declare function editorSourceDeclarations(editor: string): string[];
export declare const editorRewriteDirectories: readonly string[];
export interface EditorSourceRewrite { from: RegExp; to: string }
export declare function editorSourceRewrites(editor: string): EditorSourceRewrite[];

export declare function editorAssetFileName(asset: { originalFileNames: readonly string[] }): string;
