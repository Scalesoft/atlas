import { reactive, watch } from "vue";
import t from "../i18n/objects";
import { lazyComponent } from "../lazyComponent";
import LoadingSkeleton from "../components/LoadingSkeleton.vue";
import { boardDetailTabs, type BoardDetailTab } from "../boards/boardDetail";
import { useWorkspacePanel } from "../composables/useWorkspacePanel";
import type { DetailRow, RelationsApi, UsageApi } from "./relationsApi";

/**
 * The dock of one object (ContentDetail in the workspace panel, with the tabs of the board's object detail minus Popis): Obsah shows it
 * for a row, the tree for a reference node (a contact has no frame of its own). One panel exists, so its props are one object too.
 * `onOpen` opens a page or a board in the frame; `onFocus` (Ukázat v grafu) is there only where a graph is.
 */
export interface ObjectDetailHandlers { api?: RelationsApi; usage?: UsageApi; onOpen: (id: string, typeId: string) => void; onFocus?: (id: string) => void }

// Async, so Obsah and the tree do not load the relations graph and the editor's blocks before a dock opens; one wrapper keeps
// the identity objectDetailShown compares.
const ContentDetail = lazyComponent(() => import("../components/objects/ContentDetail.vue"), { loading: LoadingSkeleton });
const panel = useWorkspacePanel();
const tabs = boardDetailTabs.filter(tab => tab.id !== "description");
/** The props the panel's ContentDetail is bound to. */
export const detail = reactive({ row: null as DetailRow | null, tab: "overview" as BoardDetailTab, api: undefined as RelationsApi | undefined, usage: undefined as UsageApi | undefined, graph: false, onOpen: (() => {}) as ObjectDetailHandlers["onOpen"], onFocus: undefined as ObjectDetailHandlers["onFocus"] });

export const objectDetailShown = () => panel.open.value && panel.content.value === ContentDetail;
export const hideObjectDetail = () => { if (objectDetailShown()) panel.hide(); };
/** The id of the object whose dock is open, for a list to mark its row. */
export const openDetailId = () => (objectDetailShown() ? detail.row?.id : undefined);
watch(() => panel.activeTab.value, tab => { if (objectDetailShown() && tab) detail.tab = tab as BoardDetailTab; });

/** `focus` false keeps the keyboard where it is (walking the graph with the arrows shows each node's dock). */
export function showObjectDetail(row: DetailRow, handlers: ObjectDetailHandlers, focus = true) {
  Object.assign(detail, { row, tab: "overview", api: handlers.api, usage: handlers.usage, onOpen: handlers.onOpen, onFocus: handlers.onFocus, graph: !!handlers.onFocus });
  panel.show({ label: t.detail, content: ContentDetail, props: detail, tabs, activeTab: "overview", focus });
}
