<script lang="ts">
import { lazySettings } from "./lazySettings";

// Each surface in its own chunk: opening global settings does not load the space ones, and the reverse. The surfaces
// are created once for the module, not per host: defineAsyncComponent keeps the chunk it has loaded, so opening
// settings again, or switching scope or space, renders the surface at once.
const GlobalSettings = lazySettings(() => import("./global/GlobalSettings.vue"));
const SpaceSettings = lazySettings(() => import("./space/SpaceSettings.vue"));
</script>

<script setup lang="ts">
import { ref, watch } from "vue";
import UnsavedChangesDialog from "../UnsavedChangesDialog.vue";
import type { Space } from "../../api";
import type { SpaceSettings as SpaceSettingsValue } from "../../spaceSettings";
import { globalSettingsBase, spaceSettingsBase } from "../../settingsRoutes";
import { provideSettingsContext, type SettingsContext } from "./context";
import { createDirtyGuard, provideDirtyGuard } from "./dirtyGuard";
import { createSettingsFlash, provideSettingsFlash } from "./flash";
import { provideSettingsRoute } from "./useSettingsRoute";
import { createVaultRegistry, provideVaultRegistry } from "./vaultRegistry";
import { provideReauthEnvironment } from "./useReauth";
import { createApiReauthEnvironment } from "./security/apiReauth";
import { createSecurityState, provideSecurityState } from "./security/securityState";
import { createAccessRequestFeed, provideAccessRequestFeed } from "./requests/accessRequestFeed";

/**
 * The lazily loaded entry of the real settings (plan 2026-09-27): global settings for the wiki
 * administrator at `#/settings/<group>/<section>` and space settings at
 * `#/spaces/<id>/settings/<section>`. It owns what every page below shares: the hash router of
 * the surface, the unsaved-changes guard (it asks instead of blocking), the result line and the
 * vault registry. The application shell mounts one host per surface and keys it by that surface.
 */
const props = defineProps<{
  scope: "global" | "space";
  spaceId?: string;
  userId: string;
  isWikiAdmin: boolean;
  spaces: Space[];
  backupWarning?: string | null;
  version?: string;
  mcpEnabled?: boolean;
}>();
const emit = defineEmits<{
  dirty: [value: boolean];
  imported: [spaceId: string];
  warning: [value: string | null];
  openPage: [pageId: string, pdfExport?: boolean];
  newTemplate: [spaceId: string];
  newPage: [spaceId: string, options?: { title?: string }];
  spaceChanged: [value: SpaceSettingsValue];
  spaceTrashed: [spaceId: string];
  spacesChanged: [];
  accessChanged: [];
  wikiSettings: [];
  openTrash: [spaceId: string];
}>();

const guard = createDirtyGuard();
provideDirtyGuard(guard);
const pending = ref<string>();
const saving = ref(false);
const router = provideSettingsRoute(props.scope === "global" ? globalSettingsBase : spaceSettingsBase(props.spaceId ?? ""), {
  guard: (target) => {
    if (!guard.dirty.value) return true;
    pending.value = target;
    return false;
  },
});
provideSettingsFlash(createSettingsFlash(router));
provideVaultRegistry(createVaultRegistry());
// Who may do what in the security centre, and the fresh sign-in every security change asks for (V4 API).
const security = createSecurityState();
provideSecurityState(security);
provideReauthEnvironment(createApiReauthEnvironment(security));
// Pending access requests of the managed vaults, live over SignalR (accessRequestChanged), never polled.
const requests = createAccessRequestFeed();
provideAccessRequestFeed(requests);
void security.load().then(() => {
  const me = security.me.value;
  if (!me || (!me.managesAllVaults && !me.vaultManagerSpaces.length && !me.vaults.some((vault) => vault.level === "manage"))) return;
  void requests.load();
  requests.listen();
});

const context: SettingsContext = {
  get userId() {
    return props.userId;
  },
  get isWikiAdmin() {
    return props.isWikiAdmin;
  },
  get spaces() {
    return props.spaces;
  },
  get backupWarning() {
    return props.backupWarning ?? null;
  },
  get mcpEnabled() {
    return props.mcpEnabled === true;
  },
  get version() {
    return props.version;
  },
  imported: (spaceId) => emit("imported", spaceId),
  warning: (value) => emit("warning", value),
  openPage: (pageId, pdfExport) => emit("openPage", pageId, pdfExport),
  newTemplate: (spaceId) => emit("newTemplate", spaceId),
  newPage: (spaceId, options) => emit("newPage", spaceId, options),
  spaceChanged: (value) => emit("spaceChanged", value),
  spaceTrashed: (spaceId) => emit("spaceTrashed", spaceId),
  spacesChanged: () => emit("spacesChanged"),
  accessChanged: () => emit("accessChanged"),
  openWikiSettings: () => emit("wikiSettings"),
  openTrash: (spaceId) => emit("openTrash", spaceId),
};
provideSettingsContext(context);

watch(guard.dirty, (value) => emit("dirty", value), { immediate: true });
// A page that is left takes its draft with it.
watch(
  () => router.route.value.path,
  () => guard.release(),
);

function leave() {
  const target = pending.value;
  pending.value = undefined;
  if (target) router.visit(target);
}
function discardAndLeave() {
  guard.discard();
  leave();
}
async function saveAndLeave() {
  saving.value = true;
  let failed: { error: unknown } | undefined;
  try {
    await guard.save();
  } catch (error) {
    failed = { error };
  } finally {
    saving.value = false;
  }
  // A save can throw after it stored the document (a callback of the page): nothing is unsaved then and the
  // leave goes on. The error is not swallowed. A draft that is still unsaved (an invalid field, a fresh sign-in
  // the page asks for) closes the question, so the page's reason shows instead of a dialog that does nothing (UX-N-5).
  if (!guard.dirty.value) leave();
  else pending.value = undefined;
  if (failed) throw failed.error;
}
</script>

<template>
  <div class="settings-host" data-settings-host :data-scope="scope">
    <GlobalSettings v-if="scope === 'global'" />
    <SpaceSettings v-else-if="spaceId" :key="spaceId" :space-id="spaceId" />
    <UnsavedChangesDialog :open="!!pending" :saving="saving" :can-save="guard.canSave.value" @stay="pending = undefined" @discard="discardAndLeave" @save="saveAndLeave" />
  </div>
</template>

<style scoped>
.settings-host { min-width: 0; width: 100%; }
</style>
