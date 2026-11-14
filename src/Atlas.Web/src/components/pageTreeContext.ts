import type { ComputedRef, InjectionKey, Ref } from "vue";
import type { TreePage } from "../treeMove";

/**
 * What every level of the recursive page tree shares: the single roving tab stop, the visible
 * order arrow keys walk and the children index. The root derives them once from the whole page
 * list and provides them; nested instances only read them, so a deep tree costs one pass, not
 * one per expanded node.
 *
 * It lives in its own module on purpose: a key created inside `<script setup>` is rebuilt for
 * every instance, so `inject` would never find the one the parent provided, and each level would
 * silently derive its own copy.
 */
export interface PageTreeContext {
  focusedId: Ref<string | undefined>;
  order: ComputedRef<TreePage[]>;
  children: ComputedRef<Map<string | null, TreePage[]>>;
  tabStopId: ComputedRef<string | undefined>;
  /** Whether any page carries its own icon: then every row keeps an icon column. */
  iconColumn: ComputedRef<boolean>;
  /** The row whose menu is open and where it opens (its „⋯“, the row, or the pointer); one menu for the whole tree. */
  menuFor: Ref<{ page: TreePage; reference: Element | { getBoundingClientRect(): DOMRect } } | undefined>;
  /** The row being renamed in place (F2). */
  renamingId: Ref<string | undefined>;
  /** What a refused keyboard move says (read by a screen reader from the status line below the tree). */
  notice: Ref<string>;
  /** The row a dragged page was last refused over for want of the right to edit it, and when: said if the drag ends there. */
  refusedDrop: { page?: TreePage; at: number };
}

export const PAGE_TREE: InjectionKey<PageTreeContext> = Symbol("atlas-page-tree");
