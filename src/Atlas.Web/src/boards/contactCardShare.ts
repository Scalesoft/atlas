import { reactive } from "vue";

/**
 * Which contact cards of the open board show their QR code. The card (ContactObjectCard) and the board's context toolbar share it by the
 * item's id: it is a view of this tab, not a change of the board, so nothing is stored or sent and no undo step is made.
 * A card shows its code in its own place when it is big enough to be scanned from the screen, the toolbar in a dialog when it is not
 * (`QR_ROOM`, in board units = layout pixels, which a zoom does not change).
 */
const open = reactive(new Set<string>());
export const contactCardShare = {
  isOpen: (id: string) => open.has(id),
  set(id: string, on: boolean) { if (on) open.add(id); else open.delete(id); },
  toggle(id: string) { this.set(id, !open.has(id)); },
};
/** The height of a card from which it shows its code in place; a lower one gets the dialog. */
export const QR_ROOM = 120;
export const showsQrInPlace = (height: number) => height >= QR_ROOM;
