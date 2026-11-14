import { readFileSync, realpathSync } from "node:fs";
import { resolve } from "node:path";
import tailwindcss from "@tailwindcss/vite";
import vue from "@vitejs/plugin-vue";
import { normalizePath, type Plugin } from "vite";
import { defineConfig } from "vitest/config";
import { atlasBundle } from "./scripts/atlasBundle.mjs";
import { bootHtml } from "./scripts/bootHtml.mjs";
import { iconPackPart, iconPackPartCount } from "./src/iconPackPart.ts";
import {
    assertEditorSource,
    editorAssetFileName,
    editorFallbackAliases,
    editorPackageRoot,
    editorRewriteDirectories,
    editorSourceAliases,
    editorSourceEntries,
    editorSourceExports,
    editorSourceRewrites,
    editorSourceRoot,
    isEditorSourceMode,
    webRoot,
} from "./scripts/editorSourceMap.mjs";

const port = parseInt(process.env.PORT ?? "5173", 10);

const backendUrl =
    process.env.VITE_BACKEND_URL ||
    process.env["services__atlas-service__http__0"];

// Optional local editor development: ATLAS_EDITOR_MODE=source resolves the public
// `@atlas/markdown/...` imports from the editor sources (scripts/editorSourceMap.mjs).
// The default build consumes the editor's built dist (scripts/build-editor.mjs) and none of the source-mode settings apply.
const sourceMode = isEditorSourceMode();
const editor = editorSourceRoot();
if (sourceMode) assertEditorSource(editor);

/**
 * The OpenAPI plugin's Scalar bundle and the Monaco source editor with its workers are imported through
 * aliases defined only in the editor's own vite.config.ts. Apply the same rewrites, but only to imports
 * made from those source directories, so nothing else in Atlas or the editor changes resolution.
 */
function editorBundlerAliases(): Plugin {
    const directories = editorRewriteDirectories.map((path) => `${normalizePath(resolve(editor, path))}/`);
    const rewrites = editorSourceRewrites(editor);
    return {
        name: "atlas-editor-source-rewrites",
        enforce: "pre",
        resolveId(source, importer) {
            const from = importer && normalizePath(importer.split("?", 1)[0]!);
            if (!from || !directories.some((directory) => from.startsWith(directory))) return null;
            const rewrite = rewrites.find(({ from }) => from.test(source));
            return rewrite ? this.resolve(source.replace(rewrite.from, rewrite.to), importer, { skipSelf: true }) : null;
        },
    };
}

/**
 * Vitest's persistent module cache (--experimental.fsModuleCache) keys a module by its own content, but an
 * `import.meta.glob` expansion also depends on the directory listing. Vitest bails out for the literal text
 * `import.meta.glob(` only, so the typed form `import.meta.glob<T>(` (AiGalleryPage, ComponentGallery) stayed cached
 * and went stale when files were added or removed. Keep every glob user out of the persistent cache.
 */
function uncachedGlobModules(): Plugin {
    return {
        name: "atlas-uncached-glob-modules",
        configureVitest({ experimental_defineCacheKeyGenerator }) {
            experimental_defineCacheKeyGenerator(({ sourceCode }) => (/import\.meta\.glob\b/.test(sourceCode) ? false : undefined));
        },
    };
}

/**
 * `virtual:icon-pack/parts`: one loader per part of the icon pack's drawings (src/iconPackPart.ts), each part a chunk of
 * its own holding the names of src/assets/icon-pack/markup.json that hash to it. The generated file stays one file.
 */
function iconPackParts(): Plugin {
    const markup = resolve(webRoot, "src/assets/icon-pack/markup.json");
    const part = /^\0virtual:icon-pack\/part\/(\d+)$/;
    return {
        name: "atlas-icon-pack-parts",
        resolveId: (id) => (id === "virtual:icon-pack/parts" || id.startsWith("virtual:icon-pack/part/") ? `\0${id}` : null),
        load(id) {
            if (id === "\0virtual:icon-pack/parts") {
                const loaders = Array.from({ length: iconPackPartCount }, (_, index) => `() => import("virtual:icon-pack/part/${index}")`);
                return `export default [${loaders.join(", ")}];`;
            }
            const index = part.exec(id)?.[1];
            if (index === undefined) return null;
            this.addWatchFile(markup);
            const drawings: Record<string, string> = JSON.parse(readFileSync(markup, "utf8"));
            return `export default ${JSON.stringify(Object.fromEntries(Object.entries(drawings).filter(([name]) => iconPackPart(name) === Number(index))))};`;
        },
    };
}

function sourceModeResolve() {
    const dependencies = Object.keys(
        JSON.parse(readFileSync(resolve(webRoot, "package.json"), "utf8")).dependencies,
    );
    return {
        aliases: [
            ...editorSourceAliases(editor),
            // Editor source would otherwise load the editor workspace's own @tiptap/pm, and a second
            // ProseMirror copy breaks keyed plugins and decorations. A string alias covers subpaths.
            { find: "@tiptap/pm", replacement: realpathSync(resolve(webRoot, "node_modules/@tiptap/pm")) },
        ],
        // Singletons shared by Atlas and the editor source resolve from Atlas's own dependencies.
        dedupe: ["vue", "yjs", "y-protocols", ...dependencies.filter((name) => name.startsWith("@tiptap/") && name !== "@tiptap/pm")],
    };
}
const source = sourceMode ? sourceModeResolve() : undefined;
// Unreleased editor entries the current mode does not map resolve to Atlas stand-ins (feature absent).
const fallbacks = editorFallbackAliases(sourceMode ? editorSourceEntries(editor) : editorSourceExports);
// The editor release this bundle is built with; the version details show it next to the server's.
const editorVersion: string = JSON.parse(
    readFileSync(resolve(sourceMode ? editor : resolve(webRoot, "node_modules/@atlas/markdown"), "package.json"), "utf8"),
).version + (sourceMode ? "+source" : "");

export default defineConfig({
    plugins: [...(sourceMode ? [editorBundlerAliases()] : []), uncachedGlobModules(), iconPackParts(), vue(), tailwindcss(), atlasBundle(), bootHtml()],
    define: { __MARKDOWN_EDITOR_VERSION__: JSON.stringify(editorVersion) },
    resolve: {
        alias: [{ find: "@", replacement: "/src" }, ...(source?.aliases ?? []), ...fallbacks],
        ...(source ? { dedupe: source.dedupe } : {}),
    },
    // Module workers, as in the editor's own build: a dev server prepends ES imports to worker code.
    worker: { format: "es" },
    server: {
        port: port,
        // The editor package lies outside this workspace root; in source mode with ATLAS_EDITOR_SOURCE, also that checkout.
        fs: { allow: [webRoot, sourceMode ? resolve(editor, "../..") : editorPackageRoot] },
        proxy: {
            "/api": {
                target: backendUrl,
                changeOrigin: false,
                secure: false,
            },
            "/hubs": {
                target: backendUrl,
                changeOrigin: false,
                ws: true,
                secure: false,
            },
        },
    },
    build: {
        outDir: "dist", emptyOutDir: true, sourcemap: false,
        // A script inlined as a data: URL is refused by the CSP (script-src 'self'): the editor package's small scalarFrame.js
        // (the OpenAPI reference frame, referenced as new URL(…, import.meta.url)) must stay a file.
        assetsInlineLimit: (file) => (file.endsWith(".js") ? false : undefined),
        rolldownOptions: {
            input: { index: resolve(webRoot, "index.html"), print: resolve(webRoot, "print.html") },
            ...(sourceMode ? { output: { assetFileNames: editorAssetFileName } } : {}),
        },
    },
    test: {
        environment: "jsdom",
        // Scales with the machine. Measured on 64 threads: 4 workers 81 s, 16 22 s, 32 23 s, 63 25 s; past about
        // 16 the jsdom environment and module import cost grow faster than the parallel gain.
        maxWorkers: "50%",
        include: ["src/**/*.test.ts"],
        setupFiles: ["src/test-setup.ts"],
        // Node 25+ ships its own localStorage, which shadows jsdom's and breaks storage tests. The flag goes to the
        // test workers only: in NODE_OPTIONS it would also reach Playwright's bundled node, which refuses it.
        execArgv: Number(process.versions.node.split(".")[0]) >= 25 ? ["--no-webstorage"] : [],
        // Source mode: externalized node_modules would load their @tiptap imports with plain Node resolution,
        // outside the aliases/dedupe above, and create a second ProseMirror instance.
        // Package mode: the editor's dist lies outside node_modules (a workspace package) and would be inlined, i.e.
        // transformed per worker; load it with Node like any installed package instead.
        server: { deps: sourceMode ? { inline: true } : { external: [/[\/]Atlas\.Markdown[\/]dist[\/].*\.js$/] } },
    },
});
