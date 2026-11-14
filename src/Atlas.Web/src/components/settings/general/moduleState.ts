import type { StatusTone } from "../types";
import t from "../../../i18n/settings-modules";
import type { OidcSettings } from "./oidc";

/** A module as the server lists it (`/admin/modules`). `settingsUi` picks the compiled form. */
export interface ModuleDescriptor {
  id: string;
  title: string;
  description: string;
  settingsUi: string;
  order: number;
  showInSetup: boolean;
}

/** The state shown on a module's card: always an icon and a word. */
export interface ModuleState {
  tone: StatusTone;
  label: string;
  icon: string;
}

export interface YouTrackSettings {
  moduleEnabled: boolean;
  enabled: boolean;
  baseUrl: string;
  hasToken: boolean;
  version: string | null;
  spaces: { id: string; name: string }[];
  spaceProjects: { spaceId: string; projectIds: string[] }[];
  status: "disabled" | "not-configured" | "configured";
}

export function youTrackState(settings: YouTrackSettings): ModuleState {
  if (!settings.moduleEnabled) return { tone: "neutral", icon: "ban", label: t.state.moduleOff };
  if (settings.status === "configured") return { tone: "success", icon: "circle-check", label: t.state.configured };
  if (settings.enabled) return { tone: "warning", icon: "triangle-alert", label: t.state.missingConnection };
  return { tone: "neutral", icon: "clock", label: t.state.inactive };
}

export interface EditorAssistSettings {
  enabled: boolean;
  version: string | null;
}

export function editorAssistState(settings: EditorAssistSettings): ModuleState {
  return settings.enabled ? { tone: "success", icon: "circle-check", label: t.common.on } : { tone: "neutral", icon: "ban", label: t.common.off };
}

export function oidcState(settings: OidcSettings): ModuleState {
  if (settings.restartRequired) return { tone: "warning", icon: "hourglass", label: t.state.awaitingRestart };
  if (!settings.providers.length) return { tone: "neutral", icon: "clock", label: t.state.notConnected };
  return { tone: "success", icon: "circle-check", label: t.state.providers(settings.providers.length) };
}

export interface McpSettings {
  enabled: boolean;
  /** The server address; only present while the module is on. */
  url?: string | null;
  version: string | null;
}

export function mcpState(settings: McpSettings): ModuleState {
  return settings.enabled ? { tone: "success", icon: "circle-check", label: t.common.on } : { tone: "neutral", icon: "ban", label: t.common.off };
}
