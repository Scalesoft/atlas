import { shallowRef, ref, type Component } from "vue";
import type { WorkspacePanelTab } from "../components/WorkspaceSidePanel.vue";

/**
 * The single right-hand panel of the workspace (WorkspaceSidePanel). App.vue renders it; a feature
 * (the assistant, W10; the page tools of the dock, refresh 2.5) shows its content here. At most one panel at a time:
 * showing another replaces the current one.
 */
const open = ref(false);
const label = ref("");
const content = shallowRef<Component | null>(null);
/** Optional `header` slot of the panel (a title with its own menu); without it the panel shows its label. */
const header = shallowRef<Component | null>(null);
const contentProps = shallowRef<Record<string, unknown>>({});
/** Whether opening moves focus into the panel; a dock the reader did not ask for just now (a remembered one) keeps it. */
const autoFocus = ref(true);
/** The dock's tabs of a content that has them (a board object's detail); the page dock keeps its own in App.vue. */
const tabs = shallowRef<WorkspacePanelTab[] | undefined>();
const activeTab = ref<string>();

export function useWorkspacePanel() {
  return {
    open, label, content, contentProps, header, autoFocus, tabs, activeTab,
    show(next: { label: string; content: Component; props?: Record<string, unknown>; header?: Component; focus?: boolean; tabs?: WorkspacePanelTab[]; activeTab?: string }) {
      label.value = next.label; content.value = next.content; contentProps.value = next.props ?? {}; header.value = next.header ?? null;
      autoFocus.value = next.focus ?? true; tabs.value = next.tabs; activeTab.value = next.activeTab ?? next.tabs?.[0]?.id; open.value = true;
    },
    hide() { open.value = false; },
    /** Test and sign-out reset: forgets the panel together with its content. */
    reset() { open.value = false; label.value = ""; content.value = null; contentProps.value = {}; header.value = null; autoFocus.value = true; tabs.value = undefined; activeTab.value = undefined; },
  };
}
