import { h } from "vue";
import AppDialog from "./AppDialog.vue";
import LoadingSkeleton from "./LoadingSkeleton.vue";

/** A lazy component's placeholder with the shape of its target (rows, row height), so nothing moves when it arrives. */
export const skeletonOf = (props: Record<string, unknown>) => () => h(LoadingSkeleton, props);

/** A lazy dialog loads as the open dialog with its own title and size (and side width), the skeleton inside it, never in the page flow; AppDialog hands over to the real one without a second entrance. */
export const dialogSkeleton = (title: string, size: "default" | "wide" | "side", sideWidth?: string) =>
  () => h(AppDialog, { title, size, open: true, "data-loading": "", style: sideWidth ? { "--app-dialog-side-width": sideWidth } : undefined }, () => h(LoadingSkeleton, { instant: true }));

/** A lazy form or settings page loads in the column of the finished form (max 1080 px, its padding), several rows tall. */
export const formSkeleton = () =>
  h("div", { style: { width: "100%", maxWidth: "1080px", margin: "0 auto", padding: "20px var(--atlas-page-gutter, 24px) 40px", boxSizing: "border-box" } },
    h(LoadingSkeleton, { rows: 5, rowHeight: "64px" }));

/** A lazy status chip loads as a quiet pill of its size (a small StatusBadge), so the line it stands in does not move when the chip arrives. */
export const badgePlaceholder = () => h("span", { "aria-hidden": "true", "data-badge-placeholder": "", style: { display: "inline-block", width: "96px", height: "22px", borderRadius: "7px", background: "var(--hover)" } });
