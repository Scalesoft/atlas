import { type Component } from "vue";
import { lazySettings } from "../lazySettings";
import { api } from "../../../api";
import { hubApi } from "../../../hub";
import t from "../../../i18n/settings-modules";
import type { OidcSettings } from "./oidc";
import { aiModuleState } from "./ai/aiModuleState";
import { editorAssistState, mcpState, oidcState, youTrackState, type EditorAssistSettings, type McpSettings, type ModuleDescriptor, type ModuleState, type YouTrackSettings } from "./moduleState";

/**
 * Compiled module UI contributions, keyed by the descriptor's `settingsUi`. No arbitrary script,
 * URL or component name is loaded from the server: an unknown `settingsUi` has no form in this
 * client. Each form is its own chunk, loaded when its module is opened. `state` reads the one-word
 * state for the module's card on the overview.
 */
export interface ModuleEntry {
  icon: string;
  component: Component;
  state?: () => Promise<ModuleState>;
  /** The module has sections of its own: ModulesPage passes the path segment after the module id as `part`. */
  parts?: boolean;
}

export const moduleRegistry: Record<string, ModuleEntry> = {
  hub: {
    icon: "users",
    component: lazySettings(() => import("./HubModulePage.vue")),
    state: async () => {
      const settings = await hubApi.settings();
      return { tone: settings.mode === "Enforced" ? "success" : "neutral", icon: "users", label: t.hub.mode[settings.mode] };
    },
  },
  oidc: {
    icon: "identity",
    component: lazySettings(() => import("./OidcModule.vue")),
    state: async () => oidcState(await api<OidcSettings>("/admin/modules/oidc")),
  },
  youtrack: {
    icon: "box",
    component: lazySettings(() => import("./YouTrackModule.vue")),
    state: async () => youTrackState(await api<YouTrackSettings>("/admin/integrations/youtrack")),
  },
  "editor-assist": {
    icon: "lightbulb",
    component: lazySettings(() => import("./EditorAssistModule.vue")),
    state: async () => editorAssistState(await api<EditorAssistSettings>("/admin/modules/editor-assist")),
  },
  // MCP server (C1/W3): not an AI feature, so a type icon, never the AI glyph.
  mcp: {
    icon: "plug",
    component: lazySettings(() => import("./McpModule.vue")),
    state: async () => mcpState(await api<McpSettings>("/admin/mcp")),
  },
  "wiki-js-import": {
    icon: "download",
    component: lazySettings(() => import("./WikiJsImportModule.vue")),
    state: async () => ({ tone: "info", icon: "upload", label: t.state.oneTimeImport }),
  },
  // Administrace → AI (O7: a module with sections). The navigation icon is a type icon, never the AI glyph (O8):
  // the editor's icon registry has no brain/cpu, so the closest general one is the database (embeddings, index).
  ai: {
    icon: "ai",
    component: lazySettings(() => import("./ai/AiModulePage.vue")),
    parts: true,
    state: aiModuleState,
  },
  // Only in the first-run setup: the server does not list it as a module.
  "backup-import": {
    icon: "history",
    component: lazySettings(() => import("./BackupImportSection.vue")),
  },
};

export function moduleEntry(settingsUi: string | undefined): ModuleEntry | undefined {
  return settingsUi && Object.hasOwn(moduleRegistry, settingsUi) ? moduleRegistry[settingsUi] : undefined;
}
export const moduleIcon = (settingsUi: string) => moduleEntry(settingsUi)?.icon ?? "blocks";

export async function loadModules(): Promise<ModuleDescriptor[]> {
  const result = await api<{ items: ModuleDescriptor[] }>("/admin/modules");
  return [...result.items].sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
}

export type { ModuleDescriptor, ModuleState };
