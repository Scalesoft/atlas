import { api } from "../api";
import type { BoardChange } from "../boards/boardApi";
import type { BoardItem } from "../boards/boardModel";
import { arrangeChanges, deploymentMap, type DeploymentDocument } from "./deploymentMap";

/** A product or service card of a deployment (DeploymentBoardCard), what „Uspořádat“ lays out (BoardView's `layout.offers`). */
const isDeploymentCard = (item: BoardItem) => item.kind === "page" && !!item.targetResourceId && (item.props?.display === "product" || item.props?.display === "service");

/**
 * „Uspořádat jako architekturu“ of a board (BoardView passes it to the canvas with Projector on, loaded on use): the changes that put
 * the deployment cards of `ids` (none: of the whole board) into the picture a new deployment board is composed in (deploymentMap
 * arrangeChanges), the pages' pictures one under another, each read from its page as its reader reads it. A page that cannot be read
 * arranges nothing. The canvas sends them as one batch, so Ctrl+Z puts every card back.
 */
export async function arrangeDeploymentCards(items: readonly BoardItem[], ids: readonly string[]): Promise<BoardChange[]> {
  const pages = [...new Set(items.filter(item => isDeploymentCard(item) && (!ids.length || ids.includes(item.id))).map(item => item.targetResourceId!))];
  const shaped = items.map(item => ({ ...item, target: item.targetResourceId ? { resourceId: item.targetResourceId } : null }));
  const maps = await Promise.all(pages.map(async pageId => {
    const document = await api<{ contentJson: string }>(`/pages/${encodeURIComponent(pageId)}`)
      .then(page => (JSON.parse(page.contentJson) as { document?: DeploymentDocument }).document, () => undefined);
    return document ? [[pageId, deploymentMap(document)] as const] : [];
  }));
  return arrangeChanges(maps.flat(), shaped, ids);
}
