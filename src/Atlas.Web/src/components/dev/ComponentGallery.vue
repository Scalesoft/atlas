<script setup lang="ts">
import { computed, provide, ref } from "vue";
import { createFakeLiveFeed } from "../../live/fakeLiveFeed";
import { liveFeedSourceKey } from "../../live/liveFeed";
import LiveUpdateNote from "../settings/LiveUpdateNote.vue";
import InteractionStatesExample from "./InteractionStatesExample.vue";
import EditLockExample from "./EditLockExample.vue";
import PageUndoExample from "./PageUndoExample.vue";
import SaveStateExample from "./SaveStateExample.vue";
import InteractionRulesExample from "./InteractionRulesExample.vue";
import interactionRulesSource from "./InteractionRulesExample.vue?raw";
import CollaborationMotionExample from "./CollaborationMotionExample.vue";
import AtlasIcon from "../AtlasIcon.vue";
import FormField from "../FormField.vue";
import AppDialog from "../AppDialog.vue";
import KeyboardHelpDialog from "../KeyboardHelpDialog.vue";
import { boardKeymap } from "../../boards/boardKeymap";
import { requirementKeymap } from "../../projector/requirementKeymap";
import InlineRevealExample from "./InlineRevealExample.vue";
import StubbedPopoverExample from "./StubbedPopoverExample.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import LoadMoreButton from "../LoadMoreButton.vue";
import LoadingRegion from "../LoadingRegion.vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { Textarea } from "../ui/textarea";
import { Checkbox } from "../ui/checkbox";
import { Switch } from "../ui/switch";
import { Slider } from "../ui/slider";
import { Badge } from "../ui/badge";
import { Label } from "../ui/label";
import { RadioGroup, RadioGroupItem } from "../ui/radio-group";
import { Field, FieldContent, FieldDescription, FieldLabel, FieldTitle } from "../ui/field";
import { Popover, PopoverContent, PopoverTrigger } from "../ui/popover";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "../ui/tooltip";
import { Table, TableBody, TableCaption, TableCell, TableHead, TableHeader, TableRow } from "../ui/table";
import { Toaster } from "../ui/sonner";
import { toast } from "vue-sonner";
import { useToast } from "../../composables/useToast";
import ActionDisclosure from "../ActionDisclosure.vue";
import ActionDisclosureItem from "../ActionDisclosureItem.vue";
import ActionDisclosureGroup from "../ActionDisclosureGroup.vue";
import SpaceDirectory from "../SpaceDirectory.vue";
import SidebarSpace from "../SidebarSpace.vue";
import type { Space } from "../../api";
import { themeAccents } from "../../profile";
import AccountStatusBadge from "../settings/people/AccountStatusBadge.vue";
import PageStatusBadge from "../PageStatusBadge.vue";
import PageIcon from "../PageIcon.vue";
import PageTree from "../PageTree.vue";
import { resolveTreeInsert, resolveTreeMove, type TreeDrop, type TreePage } from "../../treeMove";
import { startObjectDrag, type ReferenceDrop } from "../../boards/boardDrop";
import SearchHighlight from "../SearchHighlight.vue";
import { Tabs, TabsList, TabsTrigger, TabsContent } from "../ui/tabs";
import SettingsShell from "../settings/SettingsShell.vue";
import SettingsRailExample from "./SettingsRailExample.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import SettingsRow from "../settings/SettingsRow.vue";
import SettingsList from "../settings/SettingsList.vue";
import SettingsListRow from "../settings/SettingsListRow.vue";
import TrashList from "../settings/TrashList.vue";
import StatusBadge from "../settings/StatusBadge.vue";
import ChannelBadge from "../ChannelBadge.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import ResultBanner from "../settings/ResultBanner.vue";
import HistoryChanges from "../HistoryChanges.vue";
import HistoryVersionBody from "../history/HistoryVersionBody.vue";
import HistoryActivityChart from "../history/HistoryActivityChart.vue";
import type { PageRevision, PageRevisionSummary, RevisionChanges } from "../../history";
import TileCard from "../settings/TileCard.vue";
import StatTile from "../settings/StatTile.vue";
import ActivityHeatmap from "../ActivityHeatmap.vue";
import { shiftDay } from "../../activity";
import OptionPills from "../settings/OptionPills.vue";
import SearchableTags from "../settings/SearchableTags.vue";
import SearchField from "../settings/SearchField.vue";
import AppearancePickerExample from "./AppearancePickerExample.vue";
import StepFlow from "../settings/StepFlow.vue";
import PdfCoverDetailsField from "../PdfCoverDetailsField.vue";
import PagePdfDownloadDialog from "../PagePdfDownloadDialog.vue";
import type { PdfCoverDetail } from "../../contentExport";
import ProgressiveFlow from "../settings/ProgressiveFlow.vue";
import SummaryList from "../settings/SummaryList.vue";
import { provideSettingsRoute } from "../settings/useSettingsRoute";
import type { FlowStep, SettingsNavGroup, StatusTone } from "../settings/types";
import source from "./ComponentGallery.vue?raw";
import JobsExample from "./JobsExample.vue";
import LogsExample from "./LogsExample.vue";
import logsSource from "./LogsExample.vue?raw";
import jobsSource from "./JobsExample.vue?raw";
import NetworkExample from "./NetworkExample.vue";
import networkSource from "./NetworkExample.vue?raw";
import VaultsExample from "./VaultsExample.vue";
import vaultsSource from "./VaultsExample.vue?raw";
import ModulesExample from "./ModulesExample.vue";
import BoardsExample from "./BoardsExample.vue";
import ContentsExample from "./ContentsExample.vue";
import contentsSource from "./ContentsExample.vue?raw";
import boardsSource from "./BoardsExample.vue?raw";
import BoardCardsExample from "./BoardCardsExample.vue";
import boardCardsSource from "./BoardCardsExample.vue?raw";
import BoardEditorExample from "./BoardEditorExample.vue";
import BoardAllItemsExample from "./BoardAllItemsExample.vue";
import boardAllItemsSource from "./BoardAllItemsExample.vue?raw";
import ContactsExample from "./ContactsExample.vue";
import SkillsExample from "./SkillsExample.vue";
import OverviewExample from "./OverviewExample.vue";
import RequirementsExample from "./RequirementsExample.vue";
import RequirementsModelExample from "./RequirementsModelExample.vue";
import EditorKeyboardExample from "./EditorKeyboardExample.vue";
import ChipExample from "./ChipExample.vue";
import chipSource from "./ChipExample.vue?raw";
import ChartExample from "./ChartExample.vue";
import chartSource from "./ChartExample.vue?raw";
import EditabilityExample from "./EditabilityExample.vue";
import editabilitySource from "./EditabilityExample.vue?raw";
import AttachmentExample from "./AttachmentExample.vue";
import attachmentSource from "./AttachmentExample.vue?raw";
import OpenApiCardExample from "./OpenApiCardExample.vue";
import openApiCardSource from "./OpenApiCardExample.vue?raw";
import editorKeyboardSource from "./EditorKeyboardExample.vue?raw";
import requirementsSource from "./RequirementsExample.vue?raw";
import requirementsModelSource from "./RequirementsModelExample.vue?raw";
import contactsSource from "./ContactsExample.vue?raw";
import skillsSource from "./SkillsExample.vue?raw";
import overviewSource from "./OverviewExample.vue?raw";
import PersonalExample from "./PersonalExample.vue";
import personalSource from "./PersonalExample.vue?raw";
import CommentsExample from "./CommentsExample.vue";
import commentsSource from "./CommentsExample.vue?raw";
import NotificationsExample from "./NotificationsExample.vue";
import HomeExample from "./HomeExample.vue";
import homeSource from "./HomeExample.vue?raw";
import notificationsSource from "./NotificationsExample.vue?raw";
import WhatsNewExample from "./WhatsNewExample.vue";
import whatsNewSource from "./WhatsNewExample.vue?raw";
import boardEditorSource from "./BoardEditorExample.vue?raw";
import modulesSource from "./ModulesExample.vue?raw";
import SpaceExample from "./SpaceExample.vue";
import spaceSource from "./SpaceExample.vue?raw";
import PeopleExample from "./PeopleExample.vue";
import ApiTokensExample from "./ApiTokensExample.vue";
import apiTokensSource from "./ApiTokensExample.vue?raw";
import BrandMotionExample from "./BrandMotionExample.vue";
import AiGalleryPage from "./AiGalleryPage.vue";
import RefreshGalleryPage from "./RefreshGalleryPage.vue";
import { applyGalleryMotion } from "./galleryMotion";
import peopleSource from "./PeopleExample.vue?raw";
import SecurityCenterExample from "./SecurityCenterExample.vue";
import WorkspaceSidePanelExample from "./WorkspaceSidePanelExample.vue";
import ResponsiveSettingsExample from "./ResponsiveSettingsExample.vue";
import { pragueNow } from "@atlas/markdown/core";

// Live examples use the same imports and styles as the app. No API or persisted demo state.
const router = provideSettingsRoute("#/gallery");
// The examples follow server push from an in-memory source, never the dev server's /hubs/live; the jobs and log examples provide their own.
provide(liveFeedSourceKey, createFakeLiveFeed().source);
// The prototype's two motion switches for every page (motion unification plan §6.1), applied to the root.
const { slow: motionSlow, reduced: motionReduced, systemReduced } = applyGalleryMotion();
const page = computed(() => router.route.value.segments[0] || "controls");
const name = ref("VPN pro externisty");
const rowName = ref("");
const checked = ref(true);
const enabled = ref(true);
const period = ref("week");
const pdfCover = ref<PdfCoverDetail[]>(["revision", "state", "revisionDate"]);
const pdfDownloadOpen = ref(false);
const sliderSteps = ["Rychle", "Důkladně", "Do hloubky"];
const sliderStep = ref([1]);
const pickedPeople = ref(["p-3", "system"]);
const compactPeople = ref<string[]>([]);
const galleryFilter = ref("");
const galleryPeople = [
  { value: "system", label: "Systém", icon: "server", badge: "41" },
  ...["Jana Nováková", "Petr Král", "Eva Svobodová", "Tomáš Novotný", "Lucie Dvořáková", "Marek Musil", "Karel Malý", "Alena Stará", "Filip Černý", "Zuzana Veselá", "Ondřej Veselý", "Tereza Nová"].map((label, index) => ({ value: `p-${index}`, label, icon: "account", badge: String(30 - index * 2) })),
];
const dialogOpen = ref(false), sidePanelOpen = ref(false), keyboardHelpOpen = ref(false);
// Local sample rows of the trash list: a page, a board and a space (its purge asks for the name).
const galleryTrash = ref([
  { id: "t1", title: "Stará příručka", icon: "page", detail: "Stránka · do koše 27. 10. 2026 · Jana Nováková" },
  { id: "t2", title: "Plán konference", icon: "layout-dashboard", detail: "Tabule · do koše 26. 10. 2026 · Petr Svoboda" },
  { id: "t3", title: "Archiv", icon: "book-open", detail: "12 stránek · do koše 25. 10. 2026 · Správce" },
]);
// SpaceDirectory demo: the pin flips at once and stays busy for a moment, like the shell's optimistic request.
const gallerySpaces = ref<Space[]>([
  { id: "eng", name: "Engineering", icon: "code-xml", color: "blue", role: "Admin", description: "Architektura, standardy kódu a postupy nasazení.", pinPolicy: "Default", pinned: true, pageCount: 128, lastChangedAt: "2026-09-27T14:05:00Z" },
  { id: "ops", name: "Provozní dokumentace a návody", icon: "workflow", color: "green", role: "Writer", description: "Servery, sítě, zálohy a pohotovost. Všechno, co služba potřebuje, aby běžela i v noci, a kontakty na lidi, kteří ji drží při životě.", pinPolicy: "None", pinned: false, pageCount: 46, lastChangedAt: "2026-09-25T08:30:00Z" },
  { id: "hr", name: "Lidé", icon: "compass", color: "amber", role: "Reader", pinPolicy: "Required", pinned: true, pageCount: 12, lastChangedAt: "2026-09-12T10:00:00Z" },
  { id: "legal", name: "Smlouvy", icon: "scroll-text", color: "coral", role: "Reader", itemAccessOnly: true, pinPolicy: "None", pinned: false, pageCount: 2, lastChangedAt: null },
]);
const galleryPinning = ref<ReadonlySet<string>>(new Set());
function pinGallerySpace(space: Space, pinned: boolean) {
  gallerySpaces.value = gallerySpaces.value.map((item) => (item.id === space.id ? { ...item, pinned } : item));
  galleryPinning.value = new Set([...galleryPinning.value, space.id]);
  window.setTimeout(() => { galleryPinning.value = new Set([...galleryPinning.value].filter((id) => id !== space.id)); }, 700);
}
const selectedTreePage = ref("corporate");
const collapsedTreePages = ref(new Set<string>());
const galleryTreePages: TreePage[] = [
  { id: "corporate", parentId: null, spaceId: "eng", title: "Corporate", version: 1, isPublished: true },
  { id: "covid", parentId: "corporate", spaceId: "eng", title: "COVID-19", version: 1, isPublished: true },
  { id: "hiring", parentId: "corporate", spaceId: "eng", title: "Hiring test", version: 1, isPublished: true },
  // A board (module Boards): a leaf among the pages, told apart by its icon only.
  { id: "hiring-board", parentId: "corporate", spaceId: "eng", title: "Plán náboru", version: 0, isPublished: true, canEdit: true, type: "board" },
];
// The tree composed by dragging (spec 2026-10-27 §13): reference nodes (a contact, a section, an unreadable target), a page with its
// own board's quiet mark; a drop from outside adds a node here in memory, the menu's actions only say what the shell would do.
const galleryReferenceTree = ref<TreePage[]>([
  // Its children are pinned to their titles („Řadit abecedně“): the quiet A–Z mark on its row, the ticked item in its menu.
  { id: "deploy", parentId: null, spaceId: "ref", title: "Nasazení KBN", version: 3, isPublished: true, canEdit: true, attachedBoardId: "deploy-board", childrenByTitle: true },
  { id: "deploy-db", parentId: "deploy", spaceId: "ref", title: "Databáze", version: 1, isPublished: true, canEdit: true },
  { id: "ref-contact", parentId: "deploy", spaceId: "ref", title: "Jana Nováková", version: 0, isPublished: true, canEdit: true, type: "reference", targetId: "a1b2c3d4-0000-4000-8000-000000000001", targetTypeId: "atlas.contact" },
  { id: "ref-section", parentId: null, spaceId: "ref", title: "Provozní příručka", version: 0, isPublished: true, canEdit: true, type: "reference", targetId: "3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b", targetTypeId: "atlas.page", anchor: "zalohy" },
  { id: "ref-locked", parentId: null, spaceId: "ref", title: "", version: 0, isPublished: true, canEdit: true, type: "reference", readable: false },
  { id: "plan", parentId: null, spaceId: "ref", title: "Plán náboru", version: 0, isPublished: true, canEdit: true, type: "board" },
]);
const galleryReferenceSpace: Space = { id: "ref", name: "Projekty", icon: "layers", color: "green", role: "Admin", pinPolicy: "None", pinned: true, pageCount: 3, lastChangedAt: null } as Space;
const galleryReferenceCollapsed = ref(new Set<string>());
function galleryInsert(destination: TreeDrop, target: ReferenceDrop) {
  const place = resolveTreeInsert(galleryReferenceTree.value, destination);
  if (!place) return;
  const items = [...galleryReferenceTree.value], index = place.beforePageId ? items.findIndex(item => item.id === place.beforePageId) : items.length;
  items.splice(index, 0, { id: `ref-${items.length}`, parentId: place.parentId, spaceId: "ref", title: target.title ?? "Odkaz", version: 0, isPublished: true, canEdit: true, type: "reference", targetId: target.targetId, targetTypeId: target.typeId, anchor: target.anchor ?? null });
  galleryReferenceTree.value = items;
}
const toaster = useToast();
// A row dragged or moved with Alt+arrows: moved in memory and said with Zpět, as the shell does (App.vue commitTreeMove); one toast for a run of steps.
let galleryDragged: string | undefined;
const galleryDragStart = (node: TreePage) => { galleryDragged = node.id; };
function galleryMove(destination: TreeDrop) {
  const sourceId = destination.sourceId ?? galleryDragged;
  galleryDragged = undefined;
  const before = galleryReferenceTree.value, moved = before.find(item => item.id === sourceId);
  const move = sourceId ? resolveTreeMove(before, sourceId, destination) : undefined;
  if (!move || !moved) return;
  // A hand move within a level sorted by title ends that (the server's TitleOrder); Zpět sorts it again.
  const pinned = move.parentId === moved.parentId && (move.parentId ? !!before.find(item => item.id === move.parentId)?.childrenByTitle : galleryRootByTitle.value);
  const items = before.filter(item => item !== moved).map(item => pinned && item.id === move.parentId ? { ...item, childrenByTitle: false } : item);
  const index = move.beforePageId ? items.findIndex(item => item.id === move.beforePageId) : items.length;
  items.splice(index, 0, { ...moved, parentId: move.parentId });
  galleryReferenceTree.value = items;
  if (pinned && !move.parentId) galleryRootByTitle.value = false;
  toaster.success(pinned ? "Abecední řazení zrušeno" : "Stránka byla přesunuta (jen v galerii).", { id: "gallery-tree-move", undo: () => {
    galleryReferenceTree.value = before;
    if (pinned && !move.parentId) galleryRootByTitle.value = true;
  } });
}
// „Řadit abecedně“ of the root and „Přesouvat stránky tažením“ from the space's menu; the order itself is the server's.
const galleryRootByTitle = ref(false);
const galleryDrag = ref(true);
// Rename and Odebrat ze stromu say so with Zpět, as the shell does (P6, App.vue renameTreePage / treeAction).
function galleryRename(node: TreePage, title: string) {
  const set = (value: string) => { galleryReferenceTree.value = galleryReferenceTree.value.map(item => item.id === node.id ? { ...item, title: value } : item); };
  set(title);
  toaster.success("Název byl změněn (jen v galerii).", { undo: () => set(node.title) });
}
function galleryTreeAction(action: string, node: TreePage) {
  if (action === "remove") {
    const before = galleryReferenceTree.value;
    galleryReferenceTree.value = before.filter(item => item.id !== node.id);
    toaster.success("Odkaz byl odebrán ze stromu. Cíl zůstává, kde byl.", { undo: () => { galleryReferenceTree.value = before; } });
    return;
  }
  if (action === "sortByTitle") {
    galleryReferenceTree.value = galleryReferenceTree.value.map(item => item.id === node.id ? { ...item, childrenByTitle: !node.childrenByTitle } : item);
    toaster.success(node.childrenByTitle ? "Abecední řazení vypnuto (jen v galerii)." : "Stránky se řadí abecedně (jen v galerii).");
    return;
  }
  toast(`${action}: ${node.title || "odkaz bez přístupu"} (jen v galerii).`);
}
const galleryDragContact = (event: DragEvent) => startObjectDrag(event, { id: "a1b2c3d4-0000-4000-8000-000000000002", typeId: "atlas.contact", title: "Petr Svoboda" }, { bare: true });
function toggleGalleryTree(id: string) {
  const next = new Set(collapsedTreePages.value);
  if (next.has(id)) next.delete(id); else next.add(id);
  collapsedTreePages.value = next;
}
// SidebarSpace demo: the pinned spaces (required first, each group by name) and the open one, sharing the directory's pins.
const galleryExpanded = ref(new Set(["eng"]));
const gallerySidebarSpaces = computed(() => {
  const byName = (first: Space, second: Space) => first.name.localeCompare(second.name, "cs");
  const pinned = gallerySpaces.value.filter((space) => space.pinned);
  const open = gallerySpaces.value.find((space) => space.id === "ops" && !space.pinned);
  return [...pinned.filter((space) => space.pinPolicy === "Required").sort(byName), ...pinned.filter((space) => space.pinPolicy !== "Required").sort(byName), ...(open ? [open] : [])];
});
function toggleGallerySpace(id: string) {
  const next = new Set(galleryExpanded.value);
  if (next.has(id)) next.delete(id); else next.add(id);
  galleryExpanded.value = next;
}
function setGalleryCollapsed(ids: string[], collapsed: boolean) {
  const next = new Set(collapsedTreePages.value);
  for (const id of ids) if (collapsed) next.add(id); else next.delete(id);
  collapsedTreePages.value = next;
}
// Two revisions of a page for HistoryChanges: one edited sentence, one removed and one added paragraph.
function galleryRevision(version: number, paragraphs: string[]): PageRevision {
  const content = paragraphs.map((text) => ({ type: "paragraph", content: [{ type: "text", text }] }));
  return { id: `gallery-${version}`, pageId: "gallery", version, title: "Provozní příručka", authorId: "jana", authorName: "Jana Nováková", activeFrom: "2026-09-28T10:00:00Z", activeTo: null, isCurrent: version === 2, isPublished: false, lastPublishedAt: null, markdown: paragraphs.join("\n\n"), contentJson: JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content } }) };
}
const galleryHistoryBefore = galleryRevision(1, ["Před nasazením ověřte zálohu databáze.", "Starý postup: restartujte všechny služby ručně.", "Po nasazení zkontrolujte dostupnost služby."]);
const galleryHistoryAfter = galleryRevision(2, ["Před nasazením ověřte zálohu databáze.", "Po nasazení zkontrolujte dostupnost služby a chybové logy.", "Výsledek zapište do provozního deníku."]);
// Versions of the page history (3.6-C, 5.1): a session of five people, an AI version with an MCP token, a named one, a published
// multi-contributor one and a row from before 3.6. Ids start with different hex digits, so everyone has their own colour (userColor).
const galleryChanges = (o: Partial<RevisionChanges>): RevisionChanges => ({ added: 0, removed: 0, blocks: 0, sections: [], states: [], requirementsAdded: 0, requirementsRemoved: 0, contactsAdded: [], contactsRemoved: [], attachmentsAdded: 0, attachmentsRemoved: 0, titleBefore: null, contributors: [], ...o });
const galleryVersion = (version: number, o: Partial<PageRevisionSummary>): PageRevisionSummary => ({ id: `gv-${version}`, groupId: `gg-${version}`, version, title: "Provozní příručka", authorId: galleryHistoryPeople.jana, authorName: "Jana Nováková", activeFrom: `2026-11-02T${String(8 + version).padStart(2, "0")}:15:00Z`, activeTo: null, isCurrent: false, isPublished: false, lastPublishedAt: null, checkpoints: 1, changes: null, ...o });
const galleryHistoryPeople = { jana: "00000004-0000-4000-8000-000000000001", lucie: "00000001-0000-4000-8000-000000000002", anna: "00000000-0000-4000-8000-000000000003", emma: "00000002-0000-4000-8000-000000000004", petr: "00000005-0000-4000-8000-000000000005" };
const galleryHistoryFocus = ref<string>();
const galleryVersions: PageRevisionSummary[] = [
  galleryVersion(6, { isCurrent: true, checkpoints: 14, changes: galleryChanges({ added: 64, removed: 3, blocks: 9, sections: ["Postup", "Kontrola"], contributors: [{ id: galleryHistoryPeople.jana, name: "Jana Nováková", checkpoints: 3 }, { id: galleryHistoryPeople.lucie, name: "Lucie Malá", checkpoints: 4 }, { id: galleryHistoryPeople.anna, name: "Anna Dvořáková", checkpoints: 2 }, { id: galleryHistoryPeople.emma, name: "Emma Horáková", checkpoints: 1 }, { id: galleryHistoryPeople.petr, name: "Petr Svoboda", checkpoints: 4 }] }) }),
  galleryVersion(5, { isAi: true, channel: "mcp", apiToken: { id: "t", name: "Claude Code – notebook", revoked: false }, checkpoints: 3, changes: galleryChanges({ added: 140, removed: 22, blocks: 9, states: [{ title: "A", from: "planned", to: "done" }, { title: "B", from: "planned", to: "done" }, { title: "C", from: "planned", to: "done" }], requirementsAdded: 2, sections: ["Nasazení"] }) }),
  galleryVersion(4, { label: "Před auditem", isExplicit: true, checkpoints: 12, changes: galleryChanges({ added: 12, removed: 3, blocks: 2, titleBefore: "Příručka", contactsAdded: ["Petr Svoboda"], attachmentsAdded: 1, sections: ["Kontakty"] }) }),
  galleryVersion(3, { isPublished: true, changeSummary: "Doplněn postup obnovy zálohy", checkpoints: 8, changes: galleryChanges({ added: 48, removed: 31, blocks: 5, contributors: [{ id: galleryHistoryPeople.jana, name: "Jana Nováková", checkpoints: 5 }, { id: galleryHistoryPeople.petr, name: "Petr Svoboda", checkpoints: 3 }] }) }),
  galleryVersion(2, {}),
];
const resultVisible = ref(true);
const retried = ref(false), loadMoreBusy = ref(false);
const demoLoadMore = () => { loadMoreBusy.value = true; setTimeout(() => { loadMoreBusy.value = false; }, 1200); };
// LoadingRegion demo: the first load shows a skeleton, later refreshes keep the rows in place.
const regionReady = ref(true), regionLoading = ref(false);
const regionRows = ref(["Provozní řád", "Onboarding"]);
function simulateRegionLoad(initial: boolean) {
  if (initial) regionReady.value = false;
  regionLoading.value = true;
  window.setTimeout(() => {
    regionRows.value = regionRows.value.length === 2 ? ["Provozní řád", "Onboarding", "Bezpečnost"] : ["Provozní řád", "Onboarding"];
    regionLoading.value = false; regionReady.value = true;
  }, 1200);
}
const step = ref("range");
const completed = ref(false);
const progressiveStep = ref("type");
const progressiveType = ref("");
const progressiveName = ref("");
const progressiveSaved = ref("");
const progressiveSteps = computed<FlowStep[]>(() => [
  { id: "type", label: "Druh", title: "Jaký přehled zakládáte?", icon: "layout-dashboard", ready: !!progressiveType.value, summary: progressiveType.value === "team" ? "Týmový" : "Osobní" },
  { id: "name", label: "Název", title: "Pojmenujte přehled", icon: "pencil", ready: !!progressiveName.value.trim(), summary: progressiveName.value.trim() },
  { id: "summary", label: "Souhrn", title: "Zkontrolujte a potvrďte", icon: "circle-check", nextLabel: "Potvrdit ukázku" },
]);
function confirmProgressive() {
  if (progressiveType.value && progressiveName.value.trim()) progressiveSaved.value = progressiveName.value.trim();
}
const catalogQuery = ref("");
const activityTo = pragueNow().date, activityFrom = shiftDay(activityTo, -364), activityDay = ref("");
const demoActivity = Array.from({ length: 365 }, (_, index) => ({ day: shiftDay(activityFrom, index), count: (index * 7) % 11 > 5 ? (index * 3) % 9 : 0 }));
const demoReadership = demoActivity.map((day, index) => ({ ...day, marked: index % 47 === 3 ? 1 : 0 }));
const componentSources = import.meta.glob<string>(["../**/*.vue", "!../dev/**"], { query: "?raw", import: "default" });
const catalog = computed(() => Object.keys(componentSources).sort().filter(path => path.toLowerCase().includes(catalogQuery.value.toLowerCase())));
const openedSource = ref("");
const openedPath = ref("");
async function showSource(path: string) {
  openedPath.value = path;
  openedSource.value = "";
  const text = await componentSources[path]!();
  if (openedPath.value === path) openedSource.value = text;
}
const tones: StatusTone[] = ["neutral", "accent", "info", "success", "warning", "danger"];
const navigation = [
  { path: "controls", label: "Ovládací prvky" },
  { path: "interaction-rules", label: "Pravidla ovládání" },
  { path: "palettes", label: "Barevné palety" },
  { path: "states", label: "Stavy a odezva" },
  { path: "brand", label: "Značka a pohyb" },
  { path: "ai", label: "AI a návrhy" },
  { path: "refresh", label: "Refresh 2.5" },
  { path: "layouts", label: "Základní layouty" },
  { path: "settings/networks", label: "Layout nastavení" },
  { path: "vaults", label: "Trezory a zabezpečení" },
  { path: "modules", label: "Moduly a zálohy" },
  { path: "boards", label: "Tabule" },
  { path: "boards-cards", label: "Tabule: karty a výběry" },
  { path: "boards-editor", label: "Tabule: bloky a texty" },
  { path: "boards-all", label: "Tabule: všechny prvky" },
  { path: "contents", label: "Obsah prostoru a vazby" },
  { path: "contacts", label: "Kontakty a adresář" },
  { path: "skills", label: "Mapa znalostí" },
  { path: "personal", label: "Osobní prostor a sdílení" },
  { path: "requirements", label: "Požadavky ve stránce" },
  { path: "requirements-model", label: "Požadavky: model v jednom editoru" },
  { path: "overview", label: "Přehled na stránce i na tabuli" },
  { path: "editor-keyboard", label: "Editor z klávesnice" },
  { path: "chip", label: "Čip v textu" },
  { path: "chart", label: "Graf" },
  { path: "editability", label: "Editovatelnost komponent" },
  { path: "attachment", label: "Příloha" },
  { path: "openapi-card", label: "API ve stránce" },
  { path: "comments", label: "Komentáře" },
  { path: "notifications", label: "Oznámení" },
  { path: "home", label: "Domů (přehled)" },
  { path: "whats-new", label: "Co je nového" },
  { path: "space", label: "Nastavení prostoru" },
  { path: "people", label: "Lidé" },
  { path: "api-tokens", label: "Osobní API a tokeny" },
  { path: "security-center/people/jana", label: "Bezpečnostní centrum" },
  { path: "catalog", label: "Všechny komponenty" },
];
const groups: SettingsNavGroup[] = [
  { id: "start", items: [{ path: "settings/overview", label: "Přehled", icon: "layout-dashboard", exact: true }] },
  { id: "people", label: "Lidé", items: [{ path: "settings/people", label: "Lidé", icon: "user", count: 7 }] },
  { id: "operations", label: "Provoz", items: [{ path: "settings/networks", label: "Sítě a proxy", icon: "network", count: 2 }] },
];
const steps: FlowStep[] = [
  { id: "vpn", label: "VPN", title: "Typ připojení", icon: "shield-check" },
  { id: "range", label: "Rozsah", title: "Název a adresy", hint: "Ukázka rozložení formuláře. Hodnoty se nikam neukládají.", icon: "map-pin" },
  { id: "impact", label: "Dopad", title: "Kontrola dopadu", icon: "activity" },
  { id: "confirm", label: "Potvrzení", title: "Potvrzení ukázky", icon: "circle-check", nextLabel: "Dokončit ukázku" },
];
</script>

<template>
  <TooltipProvider>
  <main class="atlas gallery settings-ui">
    <header class="gallery-header">
      <div>
        <p class="gallery-eyebrow">Atlas · pouze ve vývoji</p>
        <h1>Galerie komponent</h1>
        <p>Živé stavebnice aplikace a příklady jejich skládání. Data jsou ukázková.</p>
      </div>
      <div class="gallery-header-actions">
        <div class="gallery-motion" role="group" aria-label="Pohyb v celé galerii">
          <label class="gallery-motion-switch"><Switch v-model="motionSlow" />Zpomalit 3×</label>
          <label class="gallery-motion-switch"><Switch v-model="motionReduced" :disabled="systemReduced" />Omezený pohyb<small v-if="systemReduced">(nastaveno v systému)</small></label>
        </div>
        <a href="/" class="st-link">Zpět do aplikace <AtlasIcon name="arrow-right" :size="15" /></a>
      </div>
    </header>
    <nav class="gallery-nav" aria-label="Sekce galerie">
      <a v-for="item in navigation" :key="item.path" :href="router.href(item.path)"
         :aria-current="page === item.path.split('/')[0] ? 'page' : undefined">{{ item.label }}</a>
    </nav>

    <div class="gallery-content">
      <SettingsPage v-if="page === 'palettes'" title="Barevné palety" description="Osm klidných barev zvýraznění (profile.ts themeAccents) ve světlém i tmavém režimu. Barevná navigace rámuje bílé čtecí plochy; tmavý režim se tónuje do barvy zvýraznění a jemně září. Stavové barvy mají vždy stejný význam. Výběr pro uživatele: Já → Vzhled (AccentPicker).">
        <div v-for="mode in ['light', 'dark']" :key="mode" class="gallery-palette-grid">
          <section v-for="palette in themeAccents" :key="palette.id"
            class="atlas gallery-palette settings-ui" :data-accent="palette.id" :data-mode="mode" :aria-label="`${palette.label} · ${mode === 'light' ? 'světlý' : 'tmavý'} režim`">
            <header class="gallery-palette-heading"><h3>{{ palette.label }}</h3><span>{{ mode === 'light' ? 'Světlý' : 'Tmavý' }}</span></header>
            <div class="gallery-palette-navigation" aria-label="Výběr ve stromu: jednolitá plocha bez proužku">
              <PageTree :pages="galleryTreePages" :active-id="selectedTreePage" :collapsed-ids="collapsedTreePages" @select="selectedTreePage = $event.id" @toggle-collapsed="toggleGalleryTree" />
            </div>
            <div class="gallery-palette-paper">
              <h4>Prostor pro nápady</h4><p>Čistý obsah, výrazné akce a barevné souvislosti.</p>
              <div class="st-actions"><Button size="sm">Vytvořit stránku</Button><Button size="sm" variant="outline">Upravit</Button></div>
              <div class="st-actions"><StatusBadge tone="success" label="Aktivní" /><StatusBadge tone="info" label="Sdílené" /><StatusBadge tone="warning" label="Ke kontrole" /><StatusBadge tone="danger" label="Chyba" /></div>
              <div class="st-actions" aria-label="Barvy prostorů"><span v-for="color in ['blue', 'green', 'amber', 'coral']" :key="color" class="space-icon" :data-color="color"><AtlasIcon name="book-open" :size="18" /></span></div>
            </div>
          </section>
        </div>
      </SettingsPage>
      <SettingsPage v-else-if="page === 'controls'" title="Ovládací prvky" description="Základní komponenty importujte z @/components/ui; aplikační sestavy z @/components.">
        <SettingsSection title="Button + AtlasIcon" description="ui/button · AtlasIcon.vue — primární akce, vedlejší akce a ikony.">
          <div class="st-actions">
            <Button>Přidat</Button><Button variant="secondary">Sekundární</Button>
            <Button variant="outline">Zpět</Button><Button variant="ghost">Zrušit</Button>
            <Button variant="destructive">Odstranit</Button><Button disabled>Nedostupné</Button><Button busy><AtlasIcon name="pencil" />Ukládám…</Button>
            <Button variant="outline" size="icon" aria-label="Upravit"><AtlasIcon name="pencil" /></Button>
          </div>
        </SettingsSection>
        <SettingsSection title="Zámek úprav" description="EditLockToggle.vue — jen pro čtení (odznak a Upravit), odemčeno, pulz při pokusu o úpravu a omezený pohyb."><EditLockExample /></SettingsSection>
        <SettingsSection title="Zpět nad stránkou" description="PageUndoWedge.vue — skryto, tiché po změně, plné při najetí a fokusu s nápovědou Zpět (Ctrl+Z), stisk, po posledním kroku zmizí."><PageUndoExample /></SettingsSection>
        <SettingsSection title="Stav ukládání" description="SaveState.vue — Ukládám…, Uloženo, Offline a Chyba u stránky i tabule, které se ukládají samy."><SaveStateExample /></SettingsSection>
        <SettingsSection title="Stabilní interakce" description="Produkční karty, volby a připnutí: aktivní, nedostupný a čekající stav bez posouvání cíle pod ukazatelem."><InteractionStatesExample /></SettingsSection>
        <SettingsSection title="FormField + Input / Textarea" description="FormField.vue · ui/input · ui/textarea — jednoduchý formulář s popisky.">
          <FormField label="Název"><Input v-model="name" /></FormField>
          <FormField label="Popis"><Textarea placeholder="Doplňující informace…" /></FormField>
          <FormField label="Jen pro čtení"><Input model-value="Převzatá hodnota" readonly /></FormField>
          <FormField label="Nedostupné pole"><Input model-value="Nelze upravit" disabled /></FormField>
        </SettingsSection>
        <SettingsSection title="Checkbox + Switch + OptionPills" description="ui/checkbox · ui/switch · settings/OptionPills.vue — více voleb, přepínač a právě jedna volba.">
          <label class="gallery-choice"><Checkbox v-model="checked" /> Zahrnout podprostory</label>
          <label class="gallery-choice"><Switch v-model="enabled" /> Povolit oznámení</label>
          <OptionPills v-model="period" label="Období" :options="[{ value: 'week', label: 'Týden' }, { value: 'month', label: 'Měsíc' }, { value: 'year', label: 'Rok', disabled: true }]" />
        </SettingsSection>
        <SettingsSection title="PdfCoverDetailsField" description="PdfCoverDetailsField.vue — co titulní strana PDF uvede o revizi; ruční export (Export do PDF) i automatický export (Titulní strana) jedním polem. Předvolby (OptionPills: Všechny údaje, Formální, Bez údajů), jednotlivé údaje zaškrtávátky a vedle živý náhled titulní strany s ukázkovými údaji: zaškrtnutý údaj se do papíru rozvine, odškrtnutý složí, řádek pod ukazatelem či fokusem se na papíře zvýrazní. Samostatná komponenta, protože ji sdílí dva dialogy a náhled patří jen k ní; obecný výběr více voleb → ChoiceCards multiple.">
          <PdfCoverDetailsField v-model="pdfCover" title="Příručka Acme Care" space="Zdravotnictví" />
          <p class="st-muted">Hodnota: {{ pdfCover.length ? pdfCover.join(", ") : "žádné údaje" }}</p>
          <div><Button variant="outline" data-gallery-pdf-download @click="pdfDownloadOpen = true">Dialog Export do PDF</Button></div>
          <PagePdfDownloadDialog :open="pdfDownloadOpen" v-model="pdfCover" title="Příručka Acme Care" space="Zdravotnictví" @confirm="pdfDownloadOpen = false" @cancel="pdfDownloadOpen = false" />
        </SettingsSection>
        <SettingsSection title="Slider" description="ui/slider — uspořádané kroky jedné veličiny (méně → více). Šipky, Home/End i klik na dráhu; valueText čte krok slovem; výchozí slot kreslí do dráhy. Nezávislé alternativy bez pořadí → OptionPills; náročnost odpovědi asistenta → AiEffortMeter (AI a návrhy).">
          <div class="gallery-choice" style="max-width: 320px; gap: 12px">
            <Slider v-model="sliderStep" :min="0" :max="2" :step="1" thumb-label="Náročnost odpovědi" :value-text="(step) => sliderSteps[step]!" />
            <span style="min-width: 10ch">{{ sliderSteps[sliderStep[0]!] }}</span>
          </div>
        </SettingsSection>
        <SettingsSection title="SearchableTags" description="settings/SearchableTags.vue — více voleb z dlouhého seznamu (Kdo v auditu, 100+ lidí). Vybrané zůstávají v poli jako tagy s křížkem, shody se ukazují pod polem jako pilly, bez rozbalovací nabídky. Bez hledání nabízí jen prvních pár (řaďte podle relevance). Po napsání je první shoda předvybraná a Enter nebo Tab ji přidá (ne během skládání znaku IME ani s modifikátorem), prázdné pole nic nepřidá, šipka dolů vstoupí mezi pilly. První Esc pole vymaže, další zavře okolí; i v panelu editoru (pole to panelu řekne přes data-md-own-escape, dokud drží text). Dva lidé jednoho jména se rozliší vlastním popisem, nebo pořadím („1. z 2“), vedle počtu, i na vybraném tagu. Totéž platí pro SearchableChoiceList (Enter během načítání vzdáleného hledání počká na odpověď a vezme její první výsledek), PageLocationPicker, hledání ikon a kreseb: je to jedno klávesové chování všech hledání a výběrů (lib/searchKeys.ts, pravidla P3 a P8). Jedna krátká volba → OptionPills; jedna volba z dlouhého seznamu s popisy → SearchableChoiceList.">
          <SearchableTags v-model="pickedPeople" :options="galleryPeople" label="Kdo" placeholder="Kdokoli – hledat osobu…" />
          <p class="st-muted">Kompaktní filtr: návrhy se rozbalí až při práci s polem. První Escape pole vymaže, druhý (nebo odchod) návrhy zavře.</p>
          <SearchableTags v-model="compactPeople" :options="galleryPeople" label="Kompaktní výběr lidí" compact />
        </SettingsSection>
        <SettingsSection title="SearchField" description="settings/SearchField.vue — filtr už zobrazeného seznamu (Účty, Skupiny, Přístupy, import): ikona v poli, křížek po zadání textu, první Esc pole vymaže a zůstane v něm (v dialogu ho nezavře), další jde dál. Výběr jedné položky z dlouhého seznamu → SearchableChoiceList; více lidí najednou → SearchableTags.">
          <SearchField v-model="galleryFilter" label="Hledat účet" placeholder="Hledat jméno nebo uživatelské jméno" />
        </SettingsSection>
        <SettingsSection title="AppearancePicker" description="settings/AppearancePicker.vue — ikona a barva prostoru nebo ikony stránky z jednoho katalogu (appearance.ts), postupně (refresh 2.5): nejdřív běžné ikony a 10 rychlých barev, celá sada ikon (doporučené a balík ikon z Lucide; počty jsou v ukázce a řídí se daty) s hledáním česky i anglicky a s kategoriemi pod Další ikony a paleta 60 barev (12 odstínů × 5 tónů) pod Další barvy a tóny; volba mimo rychlé zůstane vidět. Nahoře živý náhled, jak to bude vypadat (variant tile pro prostor i s řádkem v postranním panelu, ink pro ikonu stránky). Odkrytá část je settings/AppearanceIconBrowser.vue: patří jen do AppearancePicker, dlouhý seznam lidí nebo souborů zůstává SearchableChoiceList. Hledání ikon má klávesy všech hledání: po napsání je první ikona předvybraná (data-first), Enter nebo Tab ji vezme, první Esc hledání vymaže a další zavře panel kolem. Obrázek karty prostoru je SpaceCoverField (#/gallery/refresh). Jedna krátká volba → OptionPills.">
          <AppearancePickerExample />
        </SettingsSection>
        <SettingsSection title="Tabs" description="ui/tabs — přepínání souvisejícího obsahu na jedné stránce.">
          <Tabs default-value="detail">
            <TabsList aria-label="Ukázkové záložky"><TabsTrigger value="detail">Detail</TabsTrigger><TabsTrigger value="history">Historie</TabsTrigger></TabsList>
            <TabsContent value="detail">Obsah detailu.</TabsContent><TabsContent value="history">Obsah historie.</TabsContent>
          </Tabs>
        </SettingsSection>
        <SettingsSection title="Plynulé rozbalení a vrstvené nabídky" description="InlineReveal a AppDialog — přerušení pohybu, měnící se délka obsahu, vnoření a návrat fokusu. Použijte také přepínače zpomalení a omezeného pohybu nahoře.">
          <InlineRevealExample />
        </SettingsSection>
        <SettingsSection title="Editor komponenty: skeleton, načtení, výměna" description="createMarkdownStubbedPopover (Atlas.Markdown) — editor nebo nabídka komponenty se otevře hned jako skeleton stejné velikosti a skutečný obsah se stáhne za pohybu vstupu. Nejbližší alternativa: přímo openMarkdownContextPopover, když je obsah synchronní.">
          <StubbedPopoverExample />
        </SettingsSection>
        <SettingsSection title="RadioGroup + Field + Label" description="ui/radio-group · ui/field · ui/label — volba s popisem; FormField je jednodušší obal běžného pole.">
          <RadioGroup v-model="period" aria-label="Frekvence přehledu">
            <FieldLabel for="gallery-week"><Field orientation="horizontal"><RadioGroupItem id="gallery-week" value="week" /><FieldContent><FieldTitle>Týdně</FieldTitle><FieldDescription>Souhrn jednou za týden.</FieldDescription></FieldContent></Field></FieldLabel>
            <div class="gallery-choice"><RadioGroupItem id="gallery-month" value="month" /><Label for="gallery-month">Měsíčně</Label></div>
          </RadioGroup>
        </SettingsSection>
        <SettingsSection title="ActionDisclosure / DropdownMenu + Popover + Tooltip" description="Každá nabídka se otevře s první položkou předvybranou (lib/menuFocus.ts; Enter ji provede) a má vzhled nabídky editoru: poloměr 8, zkratka vpravo, destruktivní položka červená; položka, která otevírá dialog, končí třemi tečkami. ActionDisclosure.vue i ActionMenu nosí třídy nabídky editoru, takže mají tytéž řádky a jedno zvýraznění (actionMenu.css); vzhled přichází s aplikací (@atlas/markdown/styles/menu.css), proto sedí i v nastavení a profilu, než se načte editor. Obě sdílejí pravidla zavření (lib/menuFocus.ts): jsou nemodální, takže další prvek stránky lze použít rovnou a nabídka se zavře, a zavřou se se změnou adresy i když spouštěč odroluje z pohledu. Fungují i uvnitř AppDialog: nabídka zůstane v dialogu a Esc zavře jen ji. ActionDisclosure obaluje ui/dropdown-menu, ActionMenu reka-ui přímo. Popover drží doplňující obsah, Tooltip krátkou nápovědu. Props variant, size a triggerClass dávají spouštěči vzhled tlačítka, které nahrazuje: s modulem Tabule je tak Nová stránka nabídkou Nový → Stránka / Tabule (hlavička prostoru, horní lišta, + v postranním panelu), bez modulu zůstává tlačítkem.">
          <div class="st-actions">
            <ActionDisclosure label="Akce ukázkové položky"><ActionDisclosureItem @click="dialogOpen = true">Upravit v dialogu</ActionDisclosureItem><ActionDisclosureItem disabled>Nedostupná akce</ActionDisclosureItem></ActionDisclosure>
            <!-- Groups and a destructive end, as the page's „…“ menu: ActionDisclosureGroup names a group for a screen reader and a line
                 follows it; `destructive` marks what throws work away or removes, always last. -->
            <ActionDisclosure label="Akce ukázkové stránky">
              <ActionDisclosureGroup label="Export"><ActionDisclosureItem @click="toast('Export (jen v galerii).')"><AtlasIcon name="download" />Exportovat stránku</ActionDisclosureItem></ActionDisclosureGroup>
              <ActionDisclosureGroup label="Stránka"><ActionDisclosureItem @click="toast('Historie (jen v galerii).')"><AtlasIcon name="updates" />Historie</ActionDisclosureItem></ActionDisclosureGroup>
              <ActionDisclosureGroup label="Ukončení"><ActionDisclosureItem destructive @click="toast('Do koše (jen v galerii).')"><AtlasIcon name="trash" />Přesunout do koše…</ActionDisclosureItem></ActionDisclosureGroup>
            </ActionDisclosure>
            <ActionDisclosure label="Nový obsah v prostoru" variant="default" size="default"><template #trigger><AtlasIcon name="plus" :size="15" />Nový</template><ActionDisclosureItem @click="toast('Nová stránka (jen v galerii).')"><AtlasIcon name="file-plus" />Stránka</ActionDisclosureItem><ActionDisclosureItem @click="toast('Nová tabule (jen v galerii).')"><AtlasIcon name="layout-dashboard" />Tabule</ActionDisclosureItem></ActionDisclosure>
            <Popover><PopoverTrigger as-child><Button variant="outline">Doplňující obsah</Button></PopoverTrigger><PopoverContent><p>Obsah navázaný na konkrétní ovládací prvek.</p></PopoverContent></Popover>
            <Tooltip><TooltipTrigger as-child><Button variant="outline">Nápověda</Button></TooltipTrigger><TooltipContent>Krátké vysvětlení dostupné i při zaměření klávesnicí.</TooltipContent></Tooltip>
          </div>
        </SettingsSection>
      </SettingsPage>

      <BrandMotionExample v-else-if="page === 'brand'" />
      <AiGalleryPage v-else-if="page === 'ai'" />
      <RefreshGalleryPage v-else-if="page === 'refresh'" />
      <SettingsPage v-else-if="page === 'states'" title="Stavy a odezva" description="Stav sděluje text a ikona, nikoli samotná barva.">
        <CollaborationMotionExample />
        <SettingsSection title="Badge + StatusBadge" description="ui/badge · settings/StatusBadge.vue — štítek versus stav s významem.">
          <div class="st-actions"><Badge>Výchozí</Badge><Badge variant="secondary">Sekundární</Badge><Badge variant="outline">Obrys</Badge></div>
          <div class="st-actions"><StatusBadge v-for="tone in tones" :key="tone" :tone="tone" :label="tone" /></div>
        </SettingsSection>
        <SettingsSection title="ChannelBadge" description="ChannelBadge.vue — „přes MCP“ / „přes API“ / „přes asistenta“ za autorem v Historii stránky, v Posledních publikacích a v Historii otevření. Použijte ho jen pro kanál změny; obecný StatusBadge je pro stav. Pro web a starší záznamy nevykreslí nic, token (i odvolaný) jen v detailu a jen když ho server poslal; s client řádek historie pojmenuje klienta samotným jménem tokenu (jako kurzor AI: „přes MCP · Claude Code“). Nikdy AiGlyph: MCP není AI asistent a kanál asistenta je fakt o řádku (asistent jedná jako uživatel, bez tokenu), ne výstup modelu.">
          <div class="st-actions"><ChannelBadge channel="mcp" /><ChannelBadge channel="api" /><ChannelBadge channel="assistant" /><ChannelBadge channel="web" /><ChannelBadge :channel="null" /></div>
          <div class="st-actions"><ChannelBadge channel="mcp" :token="{ id: 't1', name: 'Claude Code – notebook', revoked: false }" detailed /><ChannelBadge channel="api" :token="{ id: 't2', name: 'Skript exportu', revoked: true }" detailed /><ChannelBadge channel="import" detailed /></div>
        </SettingsSection>
        <SettingsSection title="PageStatusBadge + AccountStatusBadge" description="PageStatusBadge.vue odvozuje stav publikace (při čtení ho autor vidí u tlačítka Publikovat, viz AI galerie › Publikace), settings/people/AccountStatusBadge.vue stav účtu. Pro tyto use casy nepoužívat ručně sestavený obecný badge.">
          <div class="st-actions"><PageStatusBadge :is-published="false" :version="1" :published-version="null" /><PageStatusBadge :is-published="true" :version="2" :published-version="1" /><PageStatusBadge :is-published="true" :version="2" :published-version="2" /></div>
          <div class="st-actions"><AccountStatusBadge :account="{ isActive: true, credentialState: 'active' }" /><AccountStatusBadge :account="{ isActive: true, credentialState: 'pending' }" /><AccountStatusBadge :account="{ isActive: false, credentialState: 'active' }" /></div>
        </SettingsSection>
        <SettingsSection title="PageIcon + SearchHighlight" description="PageIcon.vue — dekorace stránky. SearchHighlight.vue — bezpečné zvýraznění nalezeného textu.">
          <div class="gallery-choice"><PageIcon :decoration="{ revisionId: 'gallery', icon: 'book-open', color: 'blue' }" /><SearchHighlight text="Provozní dokumentace a návody" query="dokumentace" /></div>
        </SettingsSection>
        <SettingsSection title="Toaster" description="ui/sonner — krátká doplňková odezva vpravo dole, s křížkem jako v aplikaci. Vratná akce má na toastu tlačítko Zpět (composables/useToast.ts): smazání, přesun do koše, přejmenování, přesun a odebrání ze stromu. Pravidlo (D3): rám nastavení a profil ukazují výsledkový pruh (flash), jinde toast; vratná akce má vždy toast se Zpět. Starý pruh notify() v App.vue zanikl. V záhlaví stránky zůstává jen krátký stav publikace (Stránka byla publikována.), dokud majitel nerozhodne, zda i ten bude oznámením; obnovení historické verze a uložení zdroje už oznamuje toast. Série kroků téhož druhu (přesun stromu Alt+šipkami) nahrazuje jediný toast (volba id). Chyba formuláře má zůstat také u dotčeného pole.">
          <div class="st-actions"><Button variant="outline" @click="toaster.success('Ukázka úspěšné akce')">Úspěch</Button><Button variant="outline" @click="toaster.success('Ukázka vratné akce', { undo: () => toast('Vráceno (jen v galerii).') })">Úspěch s akcí Zpět</Button><Button variant="outline" @click="toaster.error('Ukázka chyby')">Chyba</Button></div>
        </SettingsSection>
        <SettingsSection title="SettingsRow — validace a původ" description="settings/SettingsRow.vue — propojený popisek, nápověda, chyba a původ hodnoty. Chyba se rozbalí a zase sbalí s výškou i odstupem (settings/disclosure.ts, stejný pohyb jako InlineReveal), takže se pod ní nic neposune skokem; napište do pole název a znovu ho smažte.">
          <SettingsRow v-slot="{ id, describedBy, invalid }" label="Název" help="Jméno zobrazené v seznamu." :error="rowName.trim() ? undefined : 'Vyplňte název.'" stacked>
            <Input :id="id" v-model="rowName" :aria-describedby="describedBy" :aria-invalid="invalid" />
          </SettingsRow>
          <SettingsRow v-slot="{ id, describedBy }" label="Platnost" origin="Převzato z globálního nastavení">
            <Input :id="id" :aria-describedby="describedBy" model-value="30 dní" readonly />
          </SettingsRow>
        </SettingsSection>
        <SettingsSection title="ResultBanner + ErrorState" description="settings/ResultBanner.vue · settings/ErrorState.vue — výsledek a chyba u dotčeného obsahu. Pruh jen v rámu nastavení a v profilu (přes flash); mimo ně toast, vratná akce vždy toast se Zpět (D3). V rámu nastavení výsledek ukazuje SettingsShell (slot notice): rozbalí se s odstupem a zase sbalí, takže stránka pod ním plynule sjede a vrátí se.">
          <ResultBanner v-if="resultVisible" message="Ukázková změna je hotová." @dismiss="resultVisible = false" />
          <Button v-else variant="outline" @click="resultVisible = true">Znovu ukázat oznámení</Button>
          <ErrorState v-if="!retried" title="Ukázka chyby načítání" text="Opakování zde pouze přepne ukázkový stav." @retry="retried = true" />
          <div v-else class="st-actions"><StatusBadge tone="success" label="Načteno" /><Button variant="ghost" @click="retried = false">Znovu ukázat chybu</Button></div>
        </SettingsSection>
        <SettingsSection title="EmptyState + LoadingSkeleton" description="settings/EmptyState.vue · LoadingSkeleton.vue — prázdný obsah a průběh načítání. Ovládání (P13): holé „Načítání…“ v odstavci nebo na kartě je zakázané (kontraktní test appInteraction), místo načítaného seznamu nebo stránky drží LoadingSkeleton (řádky ve výšce skutečných řádků), jednořádkový stav LoadingNote (settings/LoadingNote.vue, také inline v řádku s počtem). EmptyState inline: jedna tlumená řádka s ikonou bez rámu pro „nic nenalezeno“ uvnitř výběrů (SearchableChoiceList, PageLocationPicker, pickery) a malých karet; odsazení určí vlastník třídou.">
          <EmptyState title="Zatím žádné položky" text="Zde bude obsah po přidání první položky." icon="folder" />
          <EmptyState inline icon="search" title="Nic neodpovídá hledání. Zkuste jiné slovo." />
          <LoadingSkeleton :rows="2" label="Ukázka načítání" />
        </SettingsSection>
        <SettingsSection title="LoadMoreButton" description="LoadMoreButton.vue — jediné tlačítko „Načíst další“ pro seznamy stránkované kurzorem nebo offsetem (D10): bez ikony, běžící načítání je stav busy tlačítka (točítko, zakázané, aria-busy), „Starší…“ (older) jen v historii. U seznamu, který už řádky ukazuje, failed přidá tichou větu „Načtení dalších selhalo“ a tlačítko se stane opakováním. Samo se nenačítá nic; automaticky se dál načítá jen hledání (jeho vlastní hlídač na konci výsledků). Ruční tlačítko ve vlastním stylu místo tohoto je zakázané.">
          <div class="flex flex-wrap items-start gap-3">
            <LoadMoreButton :busy="loadMoreBusy" @click="demoLoadMore" />
            <LoadMoreButton older />
            <LoadMoreButton failed />
          </div>
        </SettingsSection>
        <SettingsSection title="HistoryChanges" description="HistoryChanges.vue — změny verze stránky jako vykreslené bloky (Přidáno / Odstraněno / Upraveno se zvýrazněnými slovy), nezměněné bloky sbalené; přepínač Náhled | Markdown. Lišta nejdřív řekne počet změn, počty slov doplní, až je spočítá mimo hlavní vlákno; změny se vykreslují odshora po několika na snímek („Načítám další změny…“), šipky ↑ ↓ na změně nebo na tlačítkách mezi nimi skáčou. Bloky, které se liší jen neviditelně (identita nadpisu, prázdné a výchozí atributy starší revize), se za změnu nepočítají. Použití: historie stránky, schvalování konceptů, karta návrhu konceptu v asistentovi (prop label místo řádku verzí, jen Markdown; ukázka v AssistantPanel, scény Návrh úpravy sekce a Přeformátování).">
          <HistoryChanges :revision="galleryHistoryAfter" :previous="galleryHistoryBefore" />
        </SettingsSection>
        <SettingsSection title="Verze v historii stránky" description="history/HistoryVersionBody.vue + HistoryActivityChart.vue — řádek verze v historii (PageHistoryPanel): čas, autor a štítek AI se zdrojem (kanál a klient podle jména tokenu, zalomí se pod jméno), zeleně +N a červeně −M řádků, pruh velikosti (logaritmicky), popisek pojmenované verze, počet uložení, změny komponent česky, přispěvatelé s podílem; starší verze bez souhrnu zůstane bez čísel. Přispěvatelé i graf nesou barvu člověka (live/collaboratorColors themedUserColor, barva jeho kurzoru, v tmavém motivu její tmavá varianta); pruh AI verze přechází do AI inkoustu a při zobrazení jemně doroste. Graf aktivity nad seznamem kreslí renderer grafů editoru (@atlas/markdown/plugins/charts loadChartRenderer, ECharts dynamicky, vzhled ChartLook): řádky každého člověka podle podílu uložení, AI zvlášť, nad pět lidí „Ostatní“, publikace jako kosočtverce; sloupec verze pod ukazatelem nebo vybrané v seznamu zůstane plný, ostatní ztlumí (najeďte na řádek), klik na sloupec skočí na verzi. Kroky verze se rozbalují šipkou u řádku a nastupují postupně. Sekce změn a filtry (Vše, Publikované, Pojmenované, Od AI) jsou v panelu.">
          <div class="gallery-history-versions">
            <HistoryActivityChart :items="galleryVersions" :focus="galleryHistoryFocus" />
            <ul class="gallery-history-list" @mouseleave="galleryHistoryFocus = undefined"><li v-for="version in galleryVersions" :key="version.id"><div class="gallery-history-row" @mouseenter="galleryHistoryFocus = version.activeFrom"><HistoryVersionBody :item="version" /></div></li></ul>
          </div>
        </SettingsSection>
        <SettingsSection title="LoadingRegion" description="LoadingRegion.vue — seznam nebo výsledky, které se načítají a později obnovují (hledání, filtr, opakování). Stránka nikdy neposkočí.">
          <p class="st-muted">První načtení drží LoadingSkeleton v očekávané velikosti. Při obnovení zůstane zobrazený obsah na místě, ztlumí se a nahoře běží tenký pruh, který nezabírá místo. Obě indikace se ukážou až po krátké prodlevě, rychlá odpověď tedy nebliká. Obsah při obnovení nemažte. Pohyblivý indikátor musí mít místo rezervované předem: LoadingNote s <code>active</code> je vykreslený stále a jen se zviditelní, s <code>inline</code> se vejde do existujícího řádku (počet, nadpis). Kde takový řádek není, přidejte LoadingRegion <code>note</code>. Samotný LoadingSkeleton použijte pro zástupný obsah, který se neobnovuje; <code>rows</code> a <code>rowHeight</code> mu dají tvar cíle (formulář je pole a tlačítko, líný dialog se otevře s kostrou uvnitř, viz <code>loadingPlaceholders.ts</code>) a <code>instant</code> vypne nástup tam, kde kostra nahrazuje jinou.</p>
          <div class="st-actions"><Button variant="outline" :disabled="regionLoading" @click="simulateRegionLoad(true)">První načtení</Button><Button variant="outline" :disabled="regionLoading || !regionReady" @click="simulateRegionLoad(false)">Obnovit (hledání)</Button></div>
          <LoadingRegion :loading="regionLoading" :ready="regionReady" :rows="2" row-height="52px" note label="Načítání ukázky…">
            <SettingsList><SettingsListRow v-for="row in regionRows" :key="row" :title="row" /></SettingsList>
          </LoadingRegion>
        </SettingsSection>
      </SettingsPage>

      <SettingsPage v-else-if="page === 'layouts'" title="Základní layouty" description="Sestavy z existujících komponent; mřížku a rozestupy řeší rodič pomocí CSS.">
        <SettingsSection title="Postupně odkrývaný formulář" description="Další volby se objevují podle předchozích rozhodnutí na téže stránce.">
          <p>Pro složitější nastavení začněte hlavní volbou. ProgressiveFlow sjednocuje průběžné odkrývání a horní přehled u všech formulářů tohoto typu. Až podle ní odkryjte související pole přímo pod ní. Předchozí volby ponechte viditelné a upravitelné, aby uživatel stále rozuměl souvislostem.</p>
          <p><strong>Příklad:</strong> typ měření → frekvence → odpovídající časování; cílové metriky mají vlastní větev. Vedle formuláře se průběžně aktualizuje náhled. Zařízení a oznámení jsou v rozbalovacím pokročilém nastavení.</p>
          <p><strong>Použití v Atlasu:</strong> skládejte existující ChoiceCards nebo OptionPills, navazující SettingsRow a InlineReveal. Méně časté volby patří do SettingsSection s advanced; dopad může vysvětlit SummaryList nebo ImpactPreview. Nový obecný formulářový framework není potřeba.</p>
          <p>Při změně nadřazené volby zachovejte kompatibilní hodnoty a zneplatněte pouze závislé hodnoty, které už nedávají smysl. Skrytá neplatná větev nesmí ovlivnit uložení ani blokovat validaci. Změny potvrďte explicitní akcí.</p>
          <p><strong>Minimum kliknutí:</strong> výběr ihned odkryje navazující údaje. Nevyžadujte Další ani potvrzení samotné volby; explicitní akce patří uložení nebo nezbytnému potvrzení. Předchozí volby zůstávají upravitelné bez automatického posouvání a zavírání formuláře.</p>
          <p><strong>Plynulé odkrývání:</strong> každou nově otevřenou větev obalte InlineReveal, včetně závislých polí uvnitř již otevřené části. Animuje výšku a prolnutí; při omezeném pohybu pouze prolnutí. Animace nikdy nevyžaduje další kliknutí ani neblokuje ovládání. Příklad umístění níže ukazuje také přelévání barev chips při změně stavu.</p>
          <p><strong>Přehled toho, co přijde:</strong> horní chips mohou ukazovat vybranou možnost, platně vyplněné části a co zbývá doplnit. Jejich stav vychází z aktuálních údajů, nikoli z historie návštěv. Kliknutí pouze posune a zaměří otevřenou část; budoucí části jsou čitelné, ale zatím neaktivní. „Připraveno k uložení“ neznamená „Uloženo“.</p>
          <p><strong>Změna dřívější volby:</strong> již otevřený souhrn ponechte na místě, přepočítejte ho a konkrétně popište chybějící údaje. Kompatibilní hodnoty zachovejte, neplatné závislé údaje vyčistěte a odpovídající chip označte „Doplnit“. Uložení povolte až po doplnění. Do té doby tlačítko kroku zůstává v pořadí Tab (aria-disabled) a stisk vede na první neplatné pole kroku.</p>
          <p><a class="st-link" :href="router.href('vaults')">Vyzkoušet chips a změnu druhu v umístění souborů →</a> Živý příklad používá skutečný VaultLocationWizard a jeho pole.</p>
          <p><strong>Vztah k průvodci:</strong> StepFlow používejte pro skutečně oddělené fáze, které potřebují Back/Next navigaci. Samotný horní přehled ani přibývání závislých polí nejsou důvodem dělit formulář na obrazovky.</p>
          <p><strong>Vyzkoušejte přidání sítě:</strong> vyberte připojení, vyplňte název a rozsah. Objeví se souhrn a volitelné údaje; předchozí volby zůstanou upravitelné. VPN i kancelář sdílejí stejná pole, proto se při přepnutí hodnoty zachovají. Jde o krátký formulář, bez samostatné fáze kontroly dopadu.</p>
          <NetworkExample />
          <p><a class="st-link" :href="router.href('settings/networks')">Porovnat s průvodcem pro delší proces →</a> Stejné stavební prvky a pravidla polí, jiná úroveň členění procesu.</p>
          <details><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ networkSource }}</code></pre></details>
        </SettingsSection>
        <SettingsSection title="ProgressiveFlow: navazující rozhodnutí" description="settings/ProgressiveFlow.vue skládá skutečné FlowProgress a InlineReveal. Použijte pro závislá pole, živý souhrn a závěrečné potvrzení na jedné stránce; samotný InlineReveal stačí pro jednu krátkou větev bez přehledu. Tok otevřený tlačítkem (Přidat přístup, Pozvat člověka) dostane autofocus: začne v prvním poli a Esc je Zrušit.">
          <p>Vyberte druh a napište název: další část se ihned plynule rozbalí, bez Další. Dřívější volby zůstávají viditelné. Chips ukazují aktuální platnost; kliknutí jen zaměří otevřenou část. Barva se přelévá a fajfka se vykreslí při dokončení.</p>
          <p>Potom název smažte: otevřený souhrn zůstane, přepočítá se, ukáže chybějící část a zablokuje potvrzení. Doplňte ho a potvrďte samostatným tlačítkem. Omezený pohyb zachová jemné prolnutí.</p>
          <ResultBanner v-if="progressiveSaved" :message="`Ukázka potvrzena: ${progressiveSaved}. Nic se neuložilo.`" @dismiss="progressiveSaved = ''" />
          <ProgressiveFlow v-model="progressiveStep" :steps="progressiveSteps" label="Ukázka průběžného formuláře" @next="confirmProgressive" @cancel="progressiveType = ''; progressiveName = ''; progressiveSaved = ''">
            <template #type><OptionPills v-model="progressiveType" :options="[{ value: 'personal', label: 'Osobní' }, { value: 'team', label: 'Týmový' }]" label="Druh přehledu" /></template>
            <template #name><SettingsRow v-slot="{ id }" label="Název přehledu"><Input :id="id" v-model="progressiveName" placeholder="Například Provoz" /></SettingsRow></template>
            <template #summary><SummaryList :items="[{ key: 'type', icon: 'layout-dashboard', label: 'Druh', value: progressiveType === 'team' ? 'Týmový' : progressiveType ? 'Osobní' : 'Vyberte druh' }, { key: 'name', icon: 'pencil', label: 'Název', value: progressiveName.trim() || 'Doplňte název' }]" label="Aktuální souhrn ukázky" /></template>
          </ProgressiveFlow>
          <p><strong>StepFlow</strong> patří procesům, kde uživatel potřebuje přejít mezi skutečně oddělenými fázemi pomocí Zpět/Další; kroky se pohybují ve směru, kterým flow jde (dál odjíždí nahoru a další přijíždí zespodu, zpět naopak). Pro kontrolu či potvrzení, které lze ukázat pod stále upravitelnými poli, použijte ProgressiveFlow. <a class="st-link" :href="router.href('settings/networks')">Ukázka oddělených fází →</a></p>
        </SettingsSection>
        <SettingsSection title="Přehled: StatTile + TileCard" description="settings/StatTile.vue · settings/TileCard.vue — čísla a rozcestník v responzivní mřížce.">
          <div class="gallery-grid">
            <StatTile label="Prostory" value="4" icon="folder"><StatusBadge tone="success" label="V pořádku" size="sm" /></StatTile>
            <StatTile label="Účty" value="7" icon="user">Aktivní uživatelé</StatTile>
            <StatTile label="Úpravy stránek" value="42" icon="pencil">
              z toho 5 s pomocí AI
              <template #chart><svg viewBox="0 0 36 36" width="46" height="46"><g transform="rotate(-90 18 18)" fill="none" stroke-width="4.5"><circle cx="18" cy="18" r="15" pathLength="100" stroke="color-mix(in srgb, var(--muted) 18%, transparent)" /><circle cx="18" cy="18" r="15" pathLength="100" stroke="var(--accent)" stroke-dasharray="12 88" /></g></svg></template>
            </StatTile>
          </div>
          <p>Slot <code>#chart</code> dá vedle čísla malý graf (prstenec, sloupečky), který jen opakuje to, co říká text; je skrytý čtečkám. Používá ho přehled auditu (ActivitySummarySection, Nastavení → Audit): dlaždice s grafy zůstanou vidět a pořadí lidí a nástrojů se rozbalí pod „Podrobnosti“. Prop <code>compact</code> ubere výšku a odsazení pro řadu čísel nad seznamem, který má zůstat na očích (Úlohy na pozadí); jinak je dlaždice stejná.</p>
          <div class="gallery-grid">
            <StatTile compact label="Běží" value="2" icon="loader-circle">ve frontě 1</StatTile>
            <StatTile compact label="Selhalo za 24 h" value="1" icon="circle-x" tone="danger">za 7 dní 3 běhy</StatTile>
          </div>
          <div class="gallery-grid">
            <TileCard title="Provoz" description="Dokumentace a správa prostoru." icon="folder" :href="router.href('settings/overview')"><template #footer>Otevřít ukázku nastavení</template></TileCard>
            <TileCard title="Znalostní báze" description="Stejná karta bez navigace." icon="book-open" />
          </div>
          <p class="gallery-caption">Kompaktní TileCard použijte pro běžné akce: ikona vedle názvu a krátkého popisu. Standardní karta ponechává prostor pro stav, další údaje a samostatnou patičku.</p>
          <div class="gallery-grid">
            <TileCard compact title="Přidat síť" description="Kancelář nebo firemní VPN." icon="network" :href="router.href('settings/networks')" />
            <TileCard compact title="Účty" description="Pozvat člověka a spravovat přístup." :href="router.href('settings/people')"><template #footer>Otevřít účty</template></TileCard>
          </div>
        </SettingsSection>
        <SettingsSection title="Přehled prostorů: SpaceDirectory" description="SpaceDirectory.vue — Knihovna → Prostory, první záložka Knihovny a úvod wiki na #/ (samostatný odkaz na všechny prostory v postranním panelu není, Knihovna je ten přehled): jeden řádek s hledáním (název a popis, bez diakritiky), FilterMenu Zobrazit (připnuté nebo podle přístupu), počet a přepínač Karty / Seznam (refresh 2.5), pod ním karty SpaceCard s úvodním obrázkem prostoru nebo zarovnaný seznam; odkazem je jen název roztažený přes kartu, takže přepínač připnutí v rohu není vnořený v odkazu. Připnuté jsou první a při přepnutí karta neuskočí; povinně připnutý prostor má zámek. Kdo spravuje aspoň jeden prostor (can-manage), vidí pod seznamem, kde se prostor připíná všem. Nadpis a Nový prostor drží LibraryPage (#/gallery/refresh). Jen props a události, API volá shell.">
          <SpaceDirectory :spaces="gallerySpaces" can-create can-manage :pending-ids="galleryPinning" @pin="pinGallerySpace" @open="space => toast(`Otevřít ${space.name} (jen v galerii).`)" @create="toast('Nový prostor (jen v galerii).')" />
        </SettingsSection>
        <SettingsSection title="Prostor v postranním panelu: SidebarSpace" description="SidebarSpace.vue — připnutý prostor v postranním panelu: řádek jako položka stromu o úroveň výš (šipka vlevo, ikona v barvě prostoru, název) vede na přehled prostoru a je cílem přetažení stránky do kořene; pod ním, když je rozbalený, jeho PageTree. Povinně připnutý má zámek, nepřipnutý otevřený prostor popisek Otevřený prostor s Připnout. Nová stránka, Sbalit/Rozbalit vše, Odepnout a pro správce prostoru Nastavení prostoru (otevře nastavení prostoru) a Připnutí pro všechny… (vede do Nastavení prostoru → Obecné) jsou v nabídce … (ActionDisclosure), která nebere názvu místo. Připnutí sdílí s ukázkou SpaceDirectory výše. S modulem Tabule (prop boards) jsou tabule uzly téhož stromu (PageTree, pole type: board): stejný řádek, výběr, klávesy a přetažení jako stránka, jen jemná ikona tabule; tabule je list, nic se do ní nevkládá a přetažením nenese kartu stránky. Nabídka … pak nabízí i Nová tabule (událost createBoard). Vlastní řádek Tabule už není: rozdělil by obsah prostoru na dva světy (katalog interakcí §2). Obsah nabídky … není vlastní: je to společný seznam SpaceMenuItems, tentýž jako v nabídce Akce prostoru v drobečkové navigaci (Nová stránka, Nová tabule, Stav obsahu pro každého editora prostoru, Nastavení, Šablony, Přístupy, Připnutí pro všechny…, Koš, Export); strom přidává jen Najít ve stromu, Sbalit vše a Odepnout. Jen props a události; stromy, rozbalení a připnutí drží shell.">
          <div class="gallery-sidebar-spaces"><ul class="sidebar-space-list" aria-label="Ukázkové prostory v postranním panelu">
            <SidebarSpace v-for="space in gallerySidebarSpaces" :key="space.id" :space="space" :pages="space.id === 'eng' ? galleryTreePages : []"
              :expanded="galleryExpanded.has(space.id)" :current="space.id === 'ops'" :active-id="selectedTreePage" :collapsed-ids="collapsedTreePages"
              :can-write="space.role !== 'Reader'" :can-manage="space.role === 'Admin'" :pin-pending="galleryPinning.has(space.id)" boards
              @open="toast(`Otevřít ${space.name} (jen v galerii).`)" @toggle="toggleGallerySpace(space.id)" @pin="pinned => pinGallerySpace(space, pinned)" @settings="toast(`Nastavení prostoru ${space.name} (jen v galerii).`)" @pin-policy="toast(`Nastavení prostoru ${space.name} → Obecné (jen v galerii).`)"
              @create-page="toast('Nová stránka (jen v galerii).')" @create-board="toast('Nová tabule (jen v galerii).')" @set-collapsed="setGalleryCollapsed" @select="selectedTreePage = $event.id" @toggle-collapsed="toggleGalleryTree" />
          </ul></div>
        </SettingsSection>
        <SettingsSection title="Strom: odkazové uzly, filtr a nabídka řádku" description="PageTree v SidebarSpace (spec 2026-10-27 §5 a §13). Levé menu se skládá přetažením: kontakt, sekci, požadavek, tabuli nasazení nebo stránku jiného stromu pusťte na řádek (třetiny: před, do, za; tabule a odkaz jsou listy) nebo na řádek prostoru a vznikne odkazový uzel — umístění, ne kopie; Odebrat ze stromu ruší jen odkaz. Uzel nese ikonu typu cíle s malou značkou odkazu, nečitelný cíl zámek bez názvu. Stránka s vlastní tabulí (nasazení) má tichou značku, která tabuli otevře (Ctrl+Enter). Písmeno na řádku otevře filtr (také Ctrl+Shift+F, lupa na dotyku, Najít ve stromu v nabídce prostoru): první shoda je předvybraná, Enter ji otevře, ↑ ↓ vybírají, první Esc napsané smaže, druhý filtr zavře a vrátí rozbalení; bez shody Hledat v celé wiki. Dokud se stránky prostoru načítají, strom drží kostru tří řádků místo holého textu. Nabídka řádku je táž editorová nabídka jako na tabuli (ActionMenu se skupinami) z vyhrazeného „⋯“, pravým tlačítkem, Ctrl+mezerníkem, Alt+Enter, Ctrl+. nebo Shift+F10; Delete koš. Ovládání (docs/interaction-rules.md): řádek je odkaz, takže první klik otevře stránku (jako se na tabuli prvek nejdřív vybere, D1) a úpravu názvu na místě začne F2, dvojklik nebo Přejmenovat v nabídce; Enter nebo klik jinam název uloží, Esc ho vrátí. Přejmenování, přesun a odebrání ukážou oznámení se Zpět (useToast), v ukázce to zkuste. Řadit abecedně (zaškrtávací položka nabídky řádku, pro kořen v nabídce … prostoru) drží přímé podstránky seřazené podle názvů i pro nové, přejmenované a sem přesunuté stránky; řadí server, takže pořadí sdílí web, API i MCP. Úroveň to tiše značí ikonou A–Z u rodiče, jen pro zapisovatele. Ruční přesun v rámci takové úrovně řazení zruší a pořadí ponechá (Abecední řazení zrušeno · Zpět). Přesouvat stránky tažením v téže nabídce (a v profilu) přetahování vypne; přesouvá se pak Přesunout… a Alt+šipkami. Proč ne nový komponent: strom, menu, filtr (SearchField + treeFilter) i přetažení jsou existující díly; nový je jen druh uzlu.">
          <div class="gallery-sidebar-spaces">
            <p class="gallery-caption"><span class="gallery-drag-source" draggable="true" @dragstart="galleryDragContact"><AtlasIcon name="user" :size="14" /> Petr Svoboda — přetáhněte do stromu</span></p>
            <ul class="sidebar-space-list" aria-label="Ukázkový strom s odkazovými uzly">
              <SidebarSpace :space="galleryReferenceSpace" :pages="galleryReferenceTree" expanded can-write can-manage boards :collapsed-ids="galleryReferenceCollapsed"
                :root-by-title="galleryRootByTitle" :drag="galleryDrag" @sort-root="value => galleryRootByTitle = value" @drag-pages="value => galleryDrag = value"
                @toggle-collapsed="id => { const next = new Set(galleryReferenceCollapsed); if (next.has(id)) next.delete(id); else next.add(id); galleryReferenceCollapsed = next; }"
                @select="node => toast(`Otevřít ${node.title} (jen v galerii).`)" @insert="galleryInsert" @action="galleryTreeAction"
                @rename="galleryRename" @drag-start="galleryDragStart" @drop="galleryMove"
                @search-wiki="query => toast(`Hledat ${query} v celé wiki (jen v galerii).`)" @root-drop="toast('Odkaz do kořene (jen v galerii).')" />
            </ul>
          </div>
        </SettingsSection>
        <SettingsSection title="Aktivita: ActivityHeatmap" description="ActivityHeatmap.vue — kalendář aktivity po dnech s výběrem dne a ovládáním šipkami. Používá ho ProfileActivity (Profil → Aktivita, kalendářní rok s detailem dne) a PageReadership (Čtenost stránky v menu stránky, zvýrazněné dny se zobrazeným heslem). Pro souhrnné číslo použijte StatTile; barvy se řídí zvoleným barevným stylem (--accent).">
          <ActivityHeatmap :days="demoActivity" :from="activityFrom" :to="activityTo" :selected="activityDay" label="Ukázkový kalendář změn" @select="activityDay = $event" />
          <p class="gallery-caption">Vybraný den: {{ activityDay || "žádný" }}</p>
          <ActivityHeatmap :days="demoReadership" :from="activityFrom" :to="activityTo" :selected="activityDay" label="Ukázkový kalendář čtenosti"
            :unit="count => `${count} zobrazení`" :mark-label="count => `${count}× zobrazené heslo`" mark-legend="Zobrazené heslo" @select="activityDay = $event" />
          <p class="gallery-caption">Čtenost stránky (PageReadership): unit pojmenuje počet, dny s <code>marked</code> mají rámeček v barvě upozornění a vysvětlí je legenda.</p>
        </SettingsSection>
        <ResponsiveSettingsExample />
        <SettingsSection title="Seznam: SettingsList + SettingsListRow" description="Název a popis vlevo, klidný stav a akce vpravo. Úprava zůstává viditelná, méně časté akce patří do nabídky. Přidání položky patří k nadpisu stránky. Při přidání a odebrání řádků (filtr, hledání) se seznam i karta kolem něj plynule zvětší nebo zmenší; nový řádek se jen vyjede, rozsvítí se až řádek, který právě vytvořil průvodce (highlighted). Méně časté akce řádku jsou jeho `menu` (RowActionsMenu uvnitř SettingsListRow: pravé tlačítko, Ctrl+mezerník, Shift+F10, Delete/F2 podle `shortcut` položky; událost select nese id); `more` přidá viditelné „⋯“ pro řádek, kde nějaká akce nemá vlastní tlačítko (na dotyku není pravé tlačítko). Šipky ↑ ↓ Home End chodí po řádcích v každém SettingsList, žádný seznam je neváže znovu.">
          <SettingsList label="Ukázkové sítě">
            <SettingsListRow title="Firemní VPN" description="10.0.0.0/16" icon="network" :menu="[{ id: 'remove', label: 'Odebrat síť', icon: 'trash', shortcut: 'Delete', destructive: true }]" menu-label="Další akce: Firemní VPN" more @select="dialogOpen = true">
              <template #meta><StatusBadge tone="success" label="Aktivní" /></template>
              <template #actions>
                <Button variant="ghost" size="sm" @click="dialogOpen = true">Upravit</Button>
              </template>
            </SettingsListRow>
            <SettingsListRow title="Externisté" description="10.4.0.0/22" icon="network">
              <template #meta><StatusBadge tone="warning" label="Ke kontrole" /></template>
            </SettingsListRow>
          </SettingsList>
        </SettingsSection>
        <SettingsSection title="Úlohy na pozadí: seznam, detail, běh" description="settings/jobs/JobsPage.vue — Správa wiki → Provoz → Úlohy na pozadí; sestava, kterou budou opakovat stránky funkcí (Zálohy, Propojit kontakty, indexace AI), ne nová komponenta.">
          <p><strong>Přehled nahoře</strong> (3.6) skládá kompaktní StatTile (<code>compact</code>) jako přehled auditu: Běží (a kolik čeká), Selhalo za 24 h (a za 7 dní), Vyžaduje pozornost (po termínu a zaseknuté: běží déle než trojnásobek obvyklé doby) a Běhy za 14 dní s malým sloupcovým grafem v CSS (jen opakuje text, ECharts tu není potřeba). Čísla počítá server v SQL a dlaždice stojí od prvního snímku s „…“, takže nic neposkočí. Dlaždice s čím ukázat je odkaz, který nastaví filtry; fokus po otevření stránky je na přehledu, Tab jde přes dlaždice k hledání a filtrům.</p>
          <p><strong>Filtry</strong> Stav, Druh a Kdy jsou FilterMenu vedle hledání jako v auditu a drží se v adrese (<code>stav</code>, <code>druh</code>, <code>kdy</code>). Stav Selhalo ukáže nad seznamem selhané běhy období s první řádkou chyby a odkazem na běh. <strong>Seznam</strong> má dvě sekce: Pravidelné úlohy a Jednorázové a na vyžádání (převody po aktualizaci, importy, běhy z události nebo ručně); jednorázové, které jsou hotové nebo nečinné, jsou sbalené pod „Hotové a nečinné“ (rozbalí se samy, když je zapnutý filtr nebo hledání). V každé sekci je nahoře, co potřebuje pozornost; selhaný řádek má červený okraj a první řádku chyby s odkazem Podrobnosti. Řádek má dvě řádky (popis úlohy je v detailu a v titulku odkazu). Když běh běží, ikona dlaždice Běží a štítku Probíhá se točí a patou řádku přejíždí tenká linka (vzor obnovy LoadingRegion); při omezeném pohybu stojí.</p>
          <p><strong>Řádek</strong> (JobRow: SettingsListRow, StatusBadge, „Spustit teď“ a nabídka řádku RowActionsMenu s Pozastavit/Obnovit a Historií; u pravidelné úlohy, která naposled neuspěla, kdy uspěla naposledy) → <strong>detail úlohy</strong> (SummaryList, Switch pozastavení, historie běhů) → <strong>detail běhu</strong> (SummaryList, StatTile s čísly ze souhrnu, ErrorState, log běhu ve stejném LogList jako Systémový log, filtrovaný podle běhu a živě, dokud běh běží; u selhaného běhu odkaz „Zobrazit log běhu“). Systémové služby jsou poslední sekce seznamu, jen pro čtení. Nad seznamem je filtr: první shoda je předvybraná (zvýrazněná, dokud je v poli fokus) a Enter ji otevře, i když přišel dřív než seznam; ↓ jde na řádky, první Esc pole vymaže. ↑/↓ chodí po řádcích, Enter na řádku otevře detail, „Zpět“ vrátí fokus na řádek, ze kterého stránka vznikla (z běhu na jeho řádek v historii). Přepínač pozastavení se přepne hned a vrátí se, když server odmítne.</p>
          <p>Stránka nic nepolluje: server řekne o každé změně běhu (useLiveFeed, kanál <code>jobs</code> na /hubs/live, JobRunChanged) a stránka si stav přečte znovu, po každém novém připojení také. Kanály odvozuje server z práv: správce prostoru slyší jen běhy svých prostorů, log jen správci wiki. V ukázce posílá události falešný zdroj (<code>createFakeLiveFeed</code>); spusťte „Propojit kontakty“ nebo „Záloha ZIP“ a sledujte stav: běh uvidíte ve frontě, běžící a hotový. Všechna data jsou ukázková a stránka dostává místo serveru prop <code>api</code>.</p>
          <p><strong>LiveUpdateNote</strong> (settings/LiveUpdateNote.vue): když živý kanál nejde (spojení spadlo, server odběr odmítl), stránka to tiše řekne a nabídne ruční obnovení; ukázaný stav zůstane a nic se neptá na pozadí. Proti ConflictNote (409, někdo změnil totéž) a ErrorState (načtení selhalo) říká jen, že stav se sám neobnoví.</p>
          <LiveUpdateNote />
          <p class="st-muted">Proti ContactLinkPage a BackupsPage, které ukazují jeden druh běhu, tahle sestava ukazuje všechny úlohy; stránky funkcí pak ukážou jen řádek posledního běhu své úlohy s odkazem na detail běhu.</p>
          <JobsExample />
          <details><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ jobsSource }}</code></pre></details>
        </SettingsSection>
        <SettingsSection title="Systémový log: log serveru" description="settings/logs/LogsPage.vue — Správa wiki → Provoz → Systémový log; prohlížeč celého logu serveru Atlasu (ILogger v databázi), jen pro správce wiki.">
          <p>Řádky jako v logu: čas, úroveň jako štítek, kategorie, zpráva; dlouhý řádek se zalomí pod zprávu, výjimka následuje po řádcích a zalomený snímek zásobníku drží odsazení. Barví jen úroveň. Filtry Úroveň a Čas (FilterMenu) a Živě jsou v adrese; zkrácené id požadavku filtruje požadavek, název úlohy otevře běh. Seznam (LogList) je týž v detailu běhu úlohy. Text jde vybrat přes více řádků, „Kopírovat N záznamů“ vezme všechny načtené.</p>
          <LogsExample />
          <details><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ logsSource }}</code></pre></details>
        </SettingsSection>
        <SettingsSection title="Koš: TrashList" description="settings/TrashList.vue — jeden seznam koše pro všechny druhy (jednotný koš): Kontakty → Koš, Koš Mého prostoru, Koš stránek prostoru i Koš prostorů ve Správě wiki. Řádek SettingsListRow s ikonou druhu, kdo a kdy; Obnovit: název a Smazat trvale: název. Odebraný řádek se prolne ven a fokus přejde na další, šipky přecházejí mezi řádky. Smazat trvale se vždy potvrzuje (ConfirmDialog, slot #purge říká, co zmizí), u prostoru až po opsání názvu (confirmName). Řádky načítá hostitel (v-model:items); restore vracející false řádek nechá (koš stránek se nejdřív zeptá, kam obnovit). Pro živé položky použijte SettingsList; nový koš nikdy nestavte vlastní. Řádek má také „Co s tím jde dělat“ (P2): na tlačítku řádku Ctrl+mezerník, Alt+Enter, Ctrl+., Shift+F10, klávesa nabídky nebo pravé tlačítko otevře tutéž editorovou nabídku (Obnovit, Smazat trvale) a Esc vrátí klávesnici na řádek. Je to RowActionsMenu (components/RowActionsMenu.vue), sdílený díl všech seznamů řádků (koš, řádek přístupu v Přístupech, adresář kontaktů, Můj prostor): proč ne nabídka stromu — ta je jedna pro celý strom s přetahem a kotví na „⋯“ řádku, řádek seznamu potřebuje jen hostitele pěti gest uvnitř sebe; kreslí ji ActionMenu s `actions`, tedy tak jako nabídku stránky a tabule, i strom stránek: bez nadpisu, dlaždice ikon, tučné názvy, zkratky jako kód a smazání červeně (jedna nabídka, jeden vzhled). Delete a F2 na řádku rovnou provedou položku, která je uvádí jako zkratku, jako ve stromu a na tabuli: tady Delete Smazat trvale (s potvrzením). Na dotyku nabídku nahrazují viditelná tlačítka řádku (žádná akce není jen na najetí).">
          <TrashList v-model:items="galleryTrash" label="Ukázkový koš" :icon="item => item.icon" :detail="item => item.detail" :restore="async () => {}" :purge="async () => {}"
            :confirm-name="item => item.icon === 'book-open' ? item.title : ''" :loaded="true" empty-text="Všechno je obnovené nebo smazané. Načtěte galerii znovu.">
            <template #purge="{ item }"><p><strong>{{ item.title }}</strong> se smaže natrvalo. <template v-if="item.icon === 'book-open'">U prostoru se nejdřív opisuje název.</template></p></template>
          </TrashList>
        </SettingsSection>
        <SettingsSection title="Dialog: AppDialog + FormField" description="AppDialog.vue · FormField.vue — sdílená hlavička, obsah a patička dialogu. size=&quot;side&quot; je panel vysouvaný zprava přes celou výšku pro široký nástroj, který dočasně nahradí stránku (Historie, OpenAPI, Čtenost stránky z menu „…“); vše, co má zůstat vedle stránky (detail, diskuse, asistent, osoba), je dok (WorkspaceSidePanel); obsah si sám řídí rozložení a posouvání, šířku upraví --app-dialog-side-width. size=&quot;wide&quot; má pevnou šířku 720 px (zástupný dialog líně načteného obsahu má už jeho velikost). Otázka nebo krátký formulář zůstává v běžném dialogu.">
          <div class="st-actions"><Button variant="outline" @click="dialogOpen = true">Otevřít dialog</Button><Button variant="outline" @click="sidePanelOpen = true">Otevřít boční panel</Button></div>
          <p>Fokus po otevření jde na první pole k psaní, jinak na první ovládací prvek obsahu; křížek v hlavičce jen tehdy, když obsah žádný nemá. Po zavření se vrací na prvek, který dialog otevřel.</p>
        </SettingsSection>
        <SettingsSection title="Klávesové zkratky: KeyboardHelpDialog" description="KeyboardHelpDialog.vue — přehled zkratek podle kontextu (? nebo Ctrl+/ kdekoli v aplikaci). Jen vypisuje: řádky bere z jednoho registru keyboard/shortcuts.ts, oddíl Editor z exportované mapy kláves editoru, oddíl Požadavky z mapy kláves bloků požadavků (jen se zapnutým Projectorem) a oddíl Tabule z mapy kláves tabule (boards/boardKeymap.ts, jen s modulem Tabule). Nejde o obecný dialog; obsah nebo formulář patří do AppDialog.">
          <div class="st-actions"><Button variant="outline" @click="keyboardHelpOpen = true">Klávesové zkratky</Button></div>
        </SettingsSection>
        <SettingsSection title="Panel vedle stránky: WorkspaceSidePanel" description="WorkspaceSidePanel.vue — nemodální pravý sloupec pracovní plochy (asistent, později Související). Nový layout, protože AppDialog size=&quot;side&quot; je modální a stránku pod sebou zneaktivní.">
          <WorkspaceSidePanelExample />
        </SettingsSection>
        <SettingsSection title="Tabulka: Table" description="ui/table — porovnatelné údaje ve sloupcích (druhá kanonická podoba je div role=table, jak ji mají Obsah a Knihovna); SettingsListRow použijte pro seznam položek s různými metadaty.">
          <Table><TableCaption>Ukázkové prostory</TableCaption><TableHeader><TableRow><TableHead>Prostor</TableHead><TableHead>Stránky</TableHead><TableHead>Stav</TableHead></TableRow></TableHeader><TableBody><TableRow><TableCell>Provoz</TableCell><TableCell>24</TableCell><TableCell><Badge variant="secondary">Aktivní</Badge></TableCell></TableRow></TableBody></Table>
        </SettingsSection>
        <SettingsSection title="Pokročilá sekce" description="SettingsSection s advanced — méně časté volby se shrnutím aktuálního stavu." advanced summary="Oznámení jsou zapnutá.">
          <SettingsRow v-slot="{ id }" label="Oznámení"><Switch :id="id" v-model="enabled" /></SettingsRow>
        </SettingsSection>
      </SettingsPage>

      <div v-else-if="page === 'settings'">
        <p class="gallery-caption">settings/SettingsShell.vue → SettingsPage → SettingsSection → ProgressiveFlow / SettingsRow; StepFlow pro oddělené fáze. Layout podle náhledu nastavení; navigace se na úzké obrazovce změní na nabídku Sekce. Hledání sekce v postranní liště (je-li kam) předvybere po napsání první sekci, Enter ji otevře, první Esc napsané smaže.</p>
        <p class="gallery-caption">Nastavení jsou tři místa, každé s vlastním vstupem, nadpisem lišty, drobečkovou navigací a cestou zpět: Profil, Nastavení prostoru (s přepínačem prostoru v ukázce lišty níže) a Správa wiki (ukázka rozložení nastavení pod lištou). Správa wiki má šest skupin: bez názvu (Přehled, Audit), Lidé, Prostory, Trezory a hesla, AI a integrace a Provoz; ukázka zobrazuje jen tři z nich.</p>
        <SettingsRailExample />
        <SettingsShell title="Správa wiki" eyebrow="Celá wiki" icon="shield-check"
          subtitle="Účty, zabezpečení a provoz všech prostorů." :groups="groups" nav-label="Ukázkové nastavení">
          <SettingsPage v-if="router.route.value.path === 'settings/overview'" title="Přehled" icon="layout-dashboard">
            <SettingsSection title="Vyžaduje pozornost" icon="triangle-alert" :count="1" count-tone="warning">
              <SettingsList label="K řešení"><SettingsListRow title="Síť čeká na kontrolu" description="Ověřte rozsah firemní VPN." icon="network" icon-tone="warning"><template #actions><a class="st-link" :href="router.href('settings/networks')">Zkontrolovat síť</a></template></SettingsListRow></SettingsList>
            </SettingsSection>
            <div class="gallery-grid"><StatTile label="Účty" value="7" /><StatTile label="Sítě" value="2" /></div>
            <SettingsSection title="Časté úkoly" flush><div class="gallery-grid">
              <TileCard compact title="Sítě a proxy" description="Otevřít ukázku průvodce." icon="network" :href="router.href('settings/networks')" />
              <TileCard compact title="Účty" description="Spravovat lidi a přístup." icon="user" :href="router.href('settings/people')" />
            </div></SettingsSection>
          </SettingsPage>
          <SettingsPage v-else-if="router.route.value.path === 'settings/people'" title="Lidé" icon="user">
            <SettingsSection title="Uživatelé"><SettingsList label="Ukázkové účty"><SettingsListRow title="Jana Nováková" description="Správce wiki" icon="user"><template #meta><StatusBadge tone="success" label="Aktivní" /></template></SettingsListRow></SettingsList></SettingsSection>
          </SettingsPage>
          <SettingsPage v-else title="Sítě a proxy" icon="network" description="Ukázka navigace a StepFlow s odděleným zadáním, kontrolou dopadu a potvrzením.">
            <p><strong>Průvodce pro delší proces:</strong> horní postup odděluje zadání sítě, kontrolu dopadu a potvrzení. Postupné odkrývání řeší závislosti polí a může fungovat i uvnitř jednoho kroku. Používejte stejné komponenty polí; dva způsoby navigace pro tentýž use case nezavádějte. <a class="st-link" :href="router.href('layouts')">Krátký formulář s odkrýváním →</a></p>
            <ResultBanner v-if="completed" message="Ukázka dokončena. Nic se neuložilo." @dismiss="completed = false" />
            <SettingsSection label="Ukázkový průvodce sítí">
              <StepFlow v-model="step" :steps="steps" label="Sítě a proxy" @cancel="step = 'range'; completed = false" @next="completed = $event.id === 'confirm'">
                <template #vpn><p>Firemní VPN — ukázková volba.</p></template>
                <template #range>
                  <SettingsRow v-slot="{ id, describedBy }" label="Název" help="Jak se síť zobrazí v seznamu, diagnostice a auditu." stacked>
                    <Input :id="id" v-model="name" :aria-describedby="describedBy" />
                  </SettingsRow>
                  <SettingsRow v-slot="{ id, describedBy }" label="Rozsah (CIDR)" help="Adresa sítě a délka prefixu." stacked>
                    <Input :id="id" model-value="10.4.0.0/22" :aria-describedby="describedBy" />
                  </SettingsRow>
                  <SettingsRow v-slot="{ id, describedBy }" label="Popis" help="Nepovinné. Komu síť patří, kdo ji spravuje." stacked>
                    <Input :id="id" placeholder="Například WireGuard, adresy klientů" :aria-describedby="describedBy" />
                  </SettingsRow>
                </template>
                <template #impact><StatusBadge tone="info" label="Ukázková data — bez skutečného dopadu" /></template>
                <template #confirm><p>Dokončení pouze zobrazí oznámení v této galerii.</p></template>
              </StepFlow>
            </SettingsSection>
          </SettingsPage>
        </SettingsShell>
      </div>

      <SecurityCenterExample v-else-if="page === 'security-center'" />

      <div v-else-if="page === 'people'">
        <PeopleExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ peopleSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'api-tokens'">
        <ApiTokensExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ apiTokensSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'space'">
        <SpaceExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ spaceSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'modules'">
        <ModulesExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ modulesSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'boards'">
        <BoardsExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ boardsSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'contents'">
        <ContentsExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ contentsSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'boards-cards'">
        <BoardCardsExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ boardCardsSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'boards-editor'">
        <BoardEditorExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ boardEditorSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'boards-all'">
        <BoardAllItemsExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ boardAllItemsSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'attachment'">
        <AttachmentExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ attachmentSource }}</code></pre></details>
      </div>
      <div v-else-if="page === 'openapi-card'">
        <OpenApiCardExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ openApiCardSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'interaction-rules'">
        <InteractionRulesExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ interactionRulesSource }}</code></pre></details>
      </div>
      <div v-else-if="page === 'editor-keyboard'">
        <EditorKeyboardExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ editorKeyboardSource }}</code></pre></details>
      </div>
      <div v-else-if="page === 'chip'">
        <ChipExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ chipSource }}</code></pre></details>
      </div>
      <div v-else-if="page === 'chart'">
        <ChartExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ chartSource }}</code></pre></details>
      </div>
      <div v-else-if="page === 'editability'">
        <EditabilityExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ editabilitySource }}</code></pre></details>
      </div>
      <div v-else-if="page === 'requirements'">
        <RequirementsExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ requirementsSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'requirements-model'">
        <RequirementsModelExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ requirementsModelSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'overview'">
        <OverviewExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ overviewSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'contacts'">
        <ContactsExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ contactsSource }}</code></pre></details>
      </div>
      <div v-else-if="page === 'skills'">
        <SkillsExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ skillsSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'personal'">
        <PersonalExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ personalSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'comments'">
        <CommentsExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ commentsSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'notifications'">
        <NotificationsExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ notificationsSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'home'">
        <HomeExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ homeSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'whats-new'">
        <WhatsNewExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ whatsNewSource }}</code></pre></details>
      </div>

      <div v-else-if="page === 'vaults'">
        <VaultsExample />
        <details class="gallery-source"><summary>Zdroj této sestavy</summary><pre class="gallery-example-source"><code>{{ vaultsSource }}</code></pre></details>
      </div>

      <SettingsPage v-else-if="page === 'catalog'" title="Všechny stávající komponenty" description="Úplný seznam souborů v components, včetně aplikačních obrazovek a rozpracovaných náhledů. Zdroj se načte až po otevření; obrazovky s API se v galerii nespouštějí.">
        <FormField label="Hledat komponentu nebo složku"><Input v-model="catalogQuery" type="search" placeholder="Například dialog, sharing nebo settings…" /></FormField>
        <p>{{ catalog.length }} komponent · cesty relativní k src/components</p>
        <div class="gallery-grid">
          <ul class="gallery-catalog"><li v-for="path in catalog" :key="path"><button type="button" class="st-link" @click="showSource(path)">{{ path.slice(3) }}</button></li></ul>
          <section aria-label="Zdroj vybrané komponenty" class="gallery-source gallery-catalog-source">
            <strong>{{ openedPath ? openedPath.slice(3) : 'Vyberte komponentu' }}</strong>
            <p>Posuďte její účel, props a existující použití před vytvořením další komponenty pro stejný use case.</p>
            <pre v-if="openedSource"><code>{{ openedSource }}</code></pre>
          </section>
        </div>
      </SettingsPage>

      <details class="gallery-source">
        <summary>Zdroj ukázek — importy, props a skládání komponent</summary>
        <p>src/Atlas.Web/src/components/dev/ComponentGallery.vue</p>
        <pre><code>{{ source }}</code></pre>
      </details>
    </div>
    <AppDialog title="Ukázkový boční panel" size="side" :open="sidePanelOpen" class="gallery-side-panel" @close="sidePanelOpen = false">
      <div class="gallery-side-panel-body"><p>Obsah panelu se posouvá sám; hlavička zůstává nahoře.</p><ActivityHeatmap :days="demoActivity" :from="activityFrom" :to="activityTo" label="Ukázkový kalendář v panelu" /></div>
    </AppDialog>
    <KeyboardHelpDialog :open="keyboardHelpOpen" :requirement-keymap="requirementKeymap" :board-keymap="boardKeymap" @close="keyboardHelpOpen = false" />
    <AppDialog title="Ukázkový dialog" :open="dialogOpen" @close="dialogOpen = false">
      <div class="gallery-dialog-body"><FormField label="Název"><Input v-model="name" /></FormField><p>Změna zůstává pouze v paměti galerie.</p><Popover><PopoverTrigger as-child><Button variant="outline">Náhled v dialogu</Button></PopoverTrigger><PopoverContent><p>Popover patří do vrstvy tohoto dialogu. První Escape zavře náhled a vrátí focus; další zavře dialog. Klik mimo dialog (na pozadí) ho zavře stejně jako Escape; tažení výběru ven ne.</p><Button variant="outline">Akce v náhledu</Button></PopoverContent></Popover></div>
      <template #footer><Button variant="outline" @click="dialogOpen = false">Zavřít</Button></template>
    </AppDialog>
    <Toaster position="bottom-right" close-button />
  </main>
  </TooltipProvider>
</template>

<style scoped>
.gallery-history-versions { max-width: 340px; border: 1px solid var(--line); border-radius: 8px; padding-top: 10px; background: var(--sidebar); }
.gallery-history-list { list-style: none; margin: 0; padding: 0; }
.gallery-history-row { display: grid; grid-template-columns: minmax(5ch, max-content) minmax(0, 1fr) auto; column-gap: 9px; row-gap: 3px; padding: 9px 18px; border-top: 1px solid var(--line); }
/* SidebarSpace demo: the sidebar's width and surface. */
.gallery-sidebar-spaces { width: min(256px, 100%); padding: 10px; border: 1px solid var(--line); border-radius: var(--atlas-radius-lg); background: var(--sidebar); }
.atlas.gallery.settings-ui { display: block; min-height: 100dvh; color: var(--text); background: var(--surface); font: 14px/1.5 "DM Sans Variable", "DM Sans", sans-serif; }
.gallery-palette-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(290px, 100%), 1fr)); gap: 18px; }
.atlas.gallery-palette { display: block; min-height: 0; padding: 0; overflow: hidden; border: 1px solid var(--line); border-radius: var(--atlas-radius-lg); background: var(--sidebar-fill); }
.atlas.gallery-palette[data-mode="dark"] .gallery-palette-paper { background: var(--atlas-glow) no-repeat, var(--surface); }
.gallery-palette-heading { display: flex; justify-content: space-between; align-items: center; padding: 20px; }
.gallery-palette-heading h3 { margin: 0; font-size: 17px; }
.gallery-palette-heading span { color: var(--muted); font-size: 12px; }
.gallery-palette-navigation { margin: 0 12px 12px; }
.gallery-palette-paper { display: grid; gap: 16px; margin: 0 12px 12px; padding: 18px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); }
.gallery-palette-paper h4 { margin: 0; font-size: 15px; }
.gallery-palette-paper p { margin: 0; color: var(--muted); font-size: 13px; }
.gallery-header { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 16px; padding: 28px max(24px, calc((100% - 1120px) / 2)); border-bottom: 1px solid var(--line); background: var(--sidebar); }
.gallery-header h1 { margin: 4px 0; font-size: 28px; font-weight: 700; letter-spacing: -.03em; }
.gallery-header p { margin: 0; color: var(--muted); }
.gallery-eyebrow { font-size: 12px; font-weight: 600; }
.gallery-header-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 12px 20px; }
.gallery-motion { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 18px; padding: 6px 14px; border: 1px solid var(--line); border-radius: 999px; background: var(--surface); }
.gallery-motion-switch { display: inline-flex; align-items: center; gap: 8px; font-size: 13px; }
.gallery-motion-switch small { color: var(--muted); }
.gallery-nav { display: flex; flex-wrap: wrap; gap: 6px; max-width: 1168px; margin: auto; padding: 16px 24px; }
.gallery-nav a { padding: 8px 12px; border-radius: var(--atlas-radius-md); color: var(--muted); text-decoration: none; }
.gallery-nav a:hover { background: var(--hover); }
.gallery-nav a[aria-current] { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
.gallery-nav a:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.gallery-content { max-width: 1168px; margin: auto; padding: 12px 24px 48px; }
.gallery-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(240px, 100%), 1fr)); gap: 14px; }
.gallery-choice { display: flex; align-items: center; gap: 10px; }
.gallery-caption { color: var(--muted); font-size: 12px; margin: 0 0 16px; }
.gallery-drag-source { display: inline-flex; align-items: center; gap: 6px; padding: 4px 10px; border: 1px dashed var(--line); border-radius: 999px; color: var(--text); cursor: grab; }
.gallery-source { margin-top: 32px; border-top: 1px solid var(--line); padding-top: 16px; color: var(--muted); font-size: 12px; }
.gallery-source summary { cursor: pointer; color: var(--accent); font-weight: 600; }
.gallery-source p { overflow-wrap: anywhere; }
.gallery-source pre { max-height: 480px; overflow: auto; padding: 16px; background: var(--sidebar); border-radius: var(--atlas-radius-md); }
.gallery-side-panel { --app-dialog-side-width: min(760px, calc(100vw - 64px)); }
.gallery-side-panel-body { flex: 1; min-height: 0; overflow-y: auto; display: grid; align-content: start; gap: 12px; padding: 20px 22px; }
.gallery-example-source { max-height: 400px; overflow: auto; font-size: 12px; }
.gallery-catalog { margin: 0; padding: 0; list-style: none; display: grid; gap: 6px; font-size: 12px; }
.gallery-catalog button { text-align: left; overflow-wrap: anywhere; cursor: pointer; }
.gallery-catalog-source { align-self: start; min-width: 0; margin: 0; }
@media (max-width: 600px) { .gallery-content { padding-inline: 12px; } .gallery-header { padding: 20px 16px; } .gallery-nav { padding-inline: 12px; } }
</style>
