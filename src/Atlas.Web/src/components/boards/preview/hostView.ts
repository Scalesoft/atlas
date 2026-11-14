import type { InjectionKey } from "vue";
import type { ViewHandle } from "../../../live/viewSession";

/** The view session of the board around a card (BoardView provides it): its page preview listens there, not on a channel of its own. */
export const hostViewKey: InjectionKey<() => ViewHandle | undefined> = Symbol("board-host-view");
