import { computed, ref, type Ref } from "vue";
import { errorMessage, type Page } from "../api";
import t from "../i18n/projector-page";
import { projectorApi } from "./projectorApi";

export interface ProjectorPageDeps {
  api?: Pick<typeof projectorApi, "setCatalogPage" | "createDeployment">;
  /** Opens a page, the deployment just founded. */
  openPage(pageId: string): unknown;
  /** Reads the space's tree again, so the new deployment shows in it. */
  refreshTree(spaceId: string): Promise<unknown>;
  toast: { success(message: string, options?: { action?: { label: string; run: () => void } }): void; error(message: string): void };
}

/** The page menu's „Založit nasazení“ dialog: the name asked for, then, when the copy brought warnings, the copy and its warnings. */
export interface DeploymentDialog {
  title: string;
  busy: boolean;
  error: string;
  /** The copy, once founded with warnings (grants that did not carry over): the dialog keeps them in view until it is opened. */
  created?: { id: string; title: string; warnings: string[] };
}

/**
 * Whether a stored page (its `contentJson`) holds requirements: a heading with a state (requirementHeading.ts `isRequirementHeading`), or
 * an old block until the conversion job rewrote it. A text test, so the shell neither parses the page nor loads the editor's model for it;
 * a heading's attributes are flat strings, so `[^{}]*` stays inside them.
 */
const requirementPattern = /"type":"requirement"|"type":"heading","attrs":\{[^{}]*"state":"[^"]/;
export const holdsRequirements = (contentJson: string | undefined) => !!contentJson && requirementPattern.test(contentJson);

/**
 * Module Projector on the open page (U2-4, review cycle 3 C3-3): whether it is the space's requirement catalog (the page DTO's
 * `isCatalog`, the one flag the „Katalog“ chip, both page menus and the editor read; W3-6: no catalog download per page open) and
 * founding a deployment from it, in reading as in editing.
 */
export function useProjectorPage(page: Ref<Page | undefined>, enabled: Readonly<Ref<boolean>>, deps: ProjectorPageDeps) {
  const api = deps.api ?? projectorApi;
  /** Undefined without the module or a page: no chip, and no toggle. */
  const catalogPage = computed(() => (enabled.value && page.value ? page.value.isCatalog ?? false : undefined));
  /** What the server now says the page is (this menu's toggle, the editor's summary menu through `update:catalog`). */
  function setCatalog(isCatalog: boolean, pageId = page.value?.id) {
    if (page.value && page.value.id === pageId) page.value.isCatalog = isCatalog;
  }
  async function toggleCatalogPage() {
    const current = page.value;
    if (!current || catalogPage.value === undefined) return;
    try {
      const state = await api.setCatalogPage(current.id, !catalogPage.value);
      setCatalog(state.isCatalog, current.id);
      deps.toast.success(state.isCatalog ? t.isCatalog : t.isNotCatalog);
    } catch (failure) { deps.toast.error(errorMessage(failure)); }
  }

  const deployment = ref<DeploymentDialog>();
  function askDeployment() {
    const title = page.value?.title.trim();
    deployment.value = { title: title ? `${title} — kopie` : "", busy: false, error: "" };
  }
  /**
   * Founds the deployment beside the source page (the same parent, review cycle 1, K22) and opens it (U3-4). With warnings (a
   * manager's grant that needs a fresh sign-in, grants capped at the actor's own authority) the dialog stays and lists them, as
   * the summary menu does: they say what to set by hand, which a vanishing toast would not keep.
   */
  async function createDeployment() {
    const current = page.value, asked = deployment.value;
    if (!current || !asked || asked.busy || asked.created || !asked.title.trim()) return;
    asked.busy = true; asked.error = "";
    try {
      const created = await api.createDeployment(current.id, { spaceId: current.spaceId, parentId: current.parentId ?? null, title: asked.title.trim() });
      await deps.refreshTree(current.spaceId).catch(() => {});
      if (created.warnings?.length) {
        asked.busy = false;
        asked.created = { id: created.id, title: created.title, warnings: created.warnings };
        return;
      }
      deployment.value = undefined;
      deps.toast.success(t.deploymentCreated(created.title), { action: { label: t.open, run: () => void deps.openPage(created.id) } });
      if (page.value?.id === current.id) await deps.openPage(created.id);
    } catch (failure) { asked.error = errorMessage(failure); asked.busy = false; }
  }
  /** „Otevřít“ after a copy with warnings. */
  async function openCreated() {
    const created = deployment.value?.created;
    if (!created) return;
    deployment.value = undefined;
    await deps.openPage(created.id);
  }

  return { catalogPage, setCatalog, toggleCatalogPage, deployment, askDeployment, createDeployment, openCreated };
}
