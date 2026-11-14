import { h, watch } from "vue";
import { lazyComponent } from "../lazyComponent";
import LoadingSkeleton from "../components/LoadingSkeleton.vue";
import { useWorkspacePanel } from "../composables/useWorkspacePanel";
import { useAssistant } from "./useAssistant";

// Loaded when the assistant is first opened: a wiki without AI never fetches this code.
const AssistantPanel = lazyComponent(() => import("../components/assistant/AssistantPanel.vue"), { loading: () => h(LoadingSkeleton, { rows: 5, rowHeight: "64px" }) });
const AssistantPanelHeader = lazyComponent(() => import("../components/assistant/AssistantPanelHeader.vue"));

/**
 * Ties the Assistant to the one WorkspaceSidePanel of the shell (App.vue calls this once). The state decides: opening it shows
 * the panel with the assistant's content and header, closing it (state, outage, sign-out) hides the panel, and the panel's own
 * close (✕, Escape) closes the state. Another panel taking the slot also closes the assistant, so there is at most one.
 */
export function bindAssistantPanel() {
  const panel = useWorkspacePanel();
  const assistant = useAssistant();
  watch(assistant.open, (open) => {
    if (open) panel.show({ label: "Asistent", content: AssistantPanel, header: AssistantPanelHeader });
    else if (panel.content.value === (AssistantPanel as unknown)) panel.hide();
  });
  watch(panel.open, (open) => { if (!open && assistant.open.value) assistant.close(); });
  watch(() => panel.content.value, (content) => { if (content !== (AssistantPanel as unknown) && assistant.open.value) assistant.close(); });
}
