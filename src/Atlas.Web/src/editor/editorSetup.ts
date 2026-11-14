import { onScopeDispose, ref } from "vue";
import type { YouTrackClient, YouTrackIssue } from "@atlas/markdown/plugins/youtrack";
import { api, ApiError } from "../api";

/**
 * The one editor configuration of every surface that edits text the way a page does: the page editor (DocumentEditor), a
 * board's texts and a board's editor blocks (components/boards/text, components/boards/blocks). Sharing it keeps editing a
 * note indistinguishable from editing a page: the same theme classes (caret, selection and collaborator colours come from
 * `.atlas-document-editor` with the editor theme, styles/palettes.css), the same hidden editor chrome and the same YouTrack host
 * service.
 */

/** The editor theme for the app's appearance: midnight in dark mode, forest for the green accent, else the default ocean. */
export function editorThemeClass(dark: boolean, accent?: string | null): string {
  return dark ? "md-theme-midnight" : accent === "green" ? "md-theme-forest" : "";
}

/** The page's outline lives in the page dock („Obsah“) and the „Na této stránce“ row (PageOutline), so the editor's rail stays hidden. */
export const editorOutline = { mode: "hidden", preview: false } as const;

/**
 * The editor draws no top row of its own: formatting is its selection bubble, `/`, the block handle and the keys; the host lists the
 * plugin actions in its menu and shows the presence. Its caret glides to where it goes and blinks only when idle (`smoothCaret`, as in
 * Word). Bind with `v-bind`.
 */
export const editorChrome = { toolbarPresentation: "hidden", showProfileLabel: false, showPresence: false, smoothCaret: true } as const;

/**
 * YouTrack issues for the editor's YouTrack plugin through the integration's endpoints of one space: the space decides which
 * projects are allowed (`YouTrackReadRunner`). An issue outside them resolves to null, as the plugin expects.
 */
export function createYouTrackClient(spaceId: () => string): Required<YouTrackClient> {
  const space = () => encodeURIComponent(spaceId());
  return {
    search: (query, { signal }) => api<YouTrackIssue[]>(`/integrations/youtrack/search?q=${encodeURIComponent(query)}&spaceId=${space()}`, { signal }),
    async resolve(reference, { signal }) {
      try {
        return await api<YouTrackIssue>(`/integrations/youtrack/resolve?reference=${encodeURIComponent(reference)}&spaceId=${space()}`, { signal });
      } catch (error) {
        if (error instanceof ApiError && error.status === 404) return null;
        throw error;
      }
    },
  };
}

/**
 * The editor theme of the app's current appearance, read from the root element the shell keeps it on (`data-mode`,
 * `data-accent`, App.vue), for surfaces the shell does not hand `dark`/`accent` to (a board). Follows a change of appearance.
 */
export function useEditorTheme() {
  const root = document.documentElement;
  const read = () => editorThemeClass(root.dataset.mode === "dark", root.dataset.accent);
  const theme = ref(read());
  if (typeof MutationObserver === "function") {
    const observer = new MutationObserver(() => { theme.value = read(); });
    observer.observe(root, { attributes: true, attributeFilter: ["data-mode", "data-accent"] });
    onScopeDispose(() => observer.disconnect());
  }
  return theme;
}
