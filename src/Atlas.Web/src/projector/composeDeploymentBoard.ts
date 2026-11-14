import type { Editor } from "@tiptap/core";
import { errorMessage } from "../api";
import { boardApi, maxBatchChanges, type BoardDto } from "../boards/boardApi";
import { useToast } from "../composables/useToast";
import t from "../i18n/projector";
import { organizationApi } from "../organization";
import { buildShellHashEncoded } from "../shellRoute";
import { composeChanges, deploymentMap, type DeploymentDocument } from "./deploymentMap";
import { storageOf } from "./requirementRuntime";

export const deploymentBoardTitle = "Tabule nasazení";
/** The page's own board (Board.PagePurpose): no node of the tree, read by who reads the page, changed by who edits it. */
export const deploymentBoardPurpose = "projector.board";

/** The page a board is made from: its saved document (the cards draw the page as saved) and its space. */
export interface DeploymentBoardSource { pageId: string; spaceId: string; document: DeploymentDocument; title?: string }

const open = (boardId: string) => { location.hash = buildShellHashEncoded({ view: "board", boardId }); };

/**
 * The page's board is its own (the tree's `attachedBoardId`), else one 3.5.0 made under the page in the tree: the first child board that
 * already shows a card of this page's deployment, or an empty one of the default title. `made` tells whether it shows the deployment:
 * only then is it worth opening.
 */
async function findBoard(spaceId: string, pageId: string, recent = false): Promise<{ board: BoardDto; made: boolean } | undefined> {
  const tree = await (recent ? organizationApi.recentTree(spaceId) : organizationApi.tree(spaceId));
  const own = tree.items.find(item => item.id === pageId)?.attachedBoardId;
  for (const id of own ? [own] : tree.items.filter(item => item.type === "board" && item.parentId === pageId).map(item => item.id)) {
    const candidate = await boardApi.get(id);
    const made = candidate.items.some(item => item.kind === "page" && item.target?.resourceId === pageId && (item.props?.display === "product" || item.props?.display === "service"));
    if (own || made || (!candidate.items.length && candidate.title === deploymentBoardTitle)) return { board: candidate, made };
  }
}

/** The id of the page's board that shows its deployment, else undefined (what a reader may open). */
export async function pageBoardId(spaceId: string, pageId: string) {
  const found = await findBoard(spaceId, pageId, true);
  return found?.made ? found.board.id : undefined;
}

/**
 * Composes the page's board (plan 3.4 §5, 3.5.1 §1.10): its product and service cards and their connectors made by ordinary board
 * changes (composeChanges), then opened. None yet, the page's own board is made (not in the space tree). A repeated run adds only
 * what is missing.
 */
export async function composeBoardFrom(source: DeploymentBoardSource) {
  const toast = useToast(), { pageId, spaceId } = source;
  try {
    const map = deploymentMap(source.document);
    if (!map.products.length) { toast.warning(t.compose.noProducts); return; }
    const board = (await findBoard(spaceId, pageId))?.board ?? await boardApi.create(spaceId, source.title?.trim() ? `${deploymentBoardTitle} — ${source.title.trim()}`.slice(0, 200) : deploymentBoardTitle, pageId, deploymentBoardPurpose);
    const changes = composeChanges(map, pageId, board.items, () => crypto.randomUUID());
    // The server takes 200 changes a batch; cards come before their connectors, so every batch finds its ends.
    for (let at = 0; at < changes.length; at += maxBatchChanges) await boardApi.submit(board.id, crypto.randomUUID(), changes.slice(at, at + maxBatchChanges));
    open(board.id);
  } catch (error) {
    toast.error(t.compose.failed(errorMessage(error)));
  }
}

/**
 * „Tabule nasazení“ of a page's menu, palette and summary: who may edit the page composes it (adds what is missing, or makes it) and
 * opens it, as projector_compose_board does; a reader opens the made board, or learns it is not made yet.
 */
export async function openDeploymentBoard(source: DeploymentBoardSource, canCompose: boolean) {
  if (canCompose) { await composeBoardFrom(source); return; }
  const toast = useToast();
  try {
    const found = await findBoard(source.spaceId, source.pageId);
    if (found?.made) { open(found.board.id); return; }
  } catch (error) { toast.error(t.compose.notFound(errorMessage(error))); return; }
  toast.warning(t.compose.notComposed);
}

const sourceOf = (editor: Editor): DeploymentBoardSource | undefined => {
  const host = storageOf(editor).host, pageId = host?.pageId();
  return host && pageId ? { pageId, spaceId: host.spaceId(), document: editor.getJSON(), title: host.pageTitle?.() } : undefined;
};

/** The summary's „Tabule nasazení“ in the page editor: an editing one composes (or tops up) the board, then opens it; a reader's opens the made one. */
export async function composeDeploymentBoard(editor: Editor) {
  const source = sourceOf(editor);
  if (source) await openDeploymentBoard(source, editor.isEditable);
}
