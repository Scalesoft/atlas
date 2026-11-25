import { liveObjects, type LiveObjects } from "../objects/liveObjects";
import { showObjectDetail } from "../objects/objectDetail";
import { pageLink } from "../pageLinks";
import { contactTypeId } from "./contactData";

/**
 * A contact's dock (ContentDetail): its card, and a colleague's Aktivita. Opened from the directory's row, from a colleague's hit in
 * the omnisearch and by the address `#/library/contacts?contact=<id>`; every call opens it, also for the contact already open. A page
 * opened from it opens in the frame. A contact the reader cannot read opens nothing.
 */
export async function showContactDetail(id: string, store: Pick<LiveObjects, "load" | "get"> = liveObjects) {
  await store.load(id);
  const object = store.get(id);
  if (object?.available) showObjectDetail({ id, typeId: contactTypeId, title: object.title, spaceId: "" }, { onOpen: page => { location.hash = pageLink(page); } });
}
