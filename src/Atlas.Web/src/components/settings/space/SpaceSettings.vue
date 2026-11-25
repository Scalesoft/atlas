<script lang="ts">
import { lazySettings } from "../lazySettings";

// The pages, each in its own chunk as in the global settings, except the landing page (Obecné), which comes with the
// surface as the overview does in the global one. Created once for the module, not per surface: defineAsyncComponent
// keeps the chunk it has loaded, so switching space or opening settings again renders the page at once.
const AddVaultPage = lazySettings(() => import("./AddVaultPage.vue"));
const SpaceAiPage = lazySettings(() => import("./SpaceAiPage.vue"));
const SpaceTrashCleanupPage = lazySettings(() => import("./SpaceTrashCleanupPage.vue"));
const SpaceAccessPage = lazySettings(() => import("./SpaceAccessPage.vue"));
const SpaceSearchGapsPage = lazySettings(() => import("./SpaceSearchGapsPage.vue"));
const SpaceContentHealthPage = lazySettings(() => import("./SpaceContentHealthPage.vue"));
const SpaceSetsPage = lazySettings(() => import("./SpaceSetsPage.vue"));
const SpaceSharingPage = lazySettings(() => import("./SpaceSharingPage.vue"));
const SpaceVaultsPage = lazySettings(() => import("./SpaceVaultsPage.vue"));
const VaultDetailPage = lazySettings(() => import("./VaultDetailPage.vue"));
const PageTemplatesPage = lazySettings(() => import("../../PageTemplatesPage.vue"));
const AccessRequestsPage = lazySettings(() => import("../requests/AccessRequestsPage.vue"));
</script>

<script setup lang="ts">
import { computed } from "vue";
import t from "../../../i18n/settings-space";
import tv from "../../../i18n/verification";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import EmptyState from "../EmptyState.vue";
import ResultBanner from "../ResultBanner.vue";
import SettingsShell from "../SettingsShell.vue";
import { useSettingsContext } from "../context";
import { useSettingsFlash } from "../flash";
import { useSettingsRoute } from "../useSettingsRoute";
import { useVaultRegistry } from "../vaultRegistry";
import type { SettingsNavGroup } from "../types";
import { globalSettingsHome } from "../../../settingsRoutes";
import { isPlainNavigationClick } from "../../../pageLinks";
import DuplicatePagesSection from "./DuplicatePagesSection.vue";
import SpaceGeneralPage from "./SpaceGeneralPage.vue";
import { useAccessRequestFeed } from "../requests/accessRequestFeed";
import { useAiCapabilities } from "../../../composables/useAiCapabilities";
import { usePageSwap } from "../pageSwap";
import { useModules } from "../../../composables/useModules";

/**
 * Nastavení prostoru for its administrator (and the wiki administrator): Obecné, Sdílení and
 * Přístupy host the existing panels; Trezory manages the space's vaults on the vault API.
 */
const props = defineProps<{ spaceId: string }>();
const context = useSettingsContext();
const router = useSettingsRoute();
const flash = useSettingsFlash();
const registry = useVaultRegistry();
const requests = useAccessRequestFeed();
const { any: aiAvailable } = useAiCapabilities();
const spaceAiAvailable = computed(() => aiAvailable.value || context.mcpEnabled === true);
const space = computed(() => context.spaces.find((item) => item.id === props.spaceId));
// Sady a katalog (plan 3.5 C) only with the projector module, whose catalog and sets it shows.
const projector = computed(() => useModules().enabled("projector"));

const groups = computed<SettingsNavGroup[]>(() => {
  const vaults = registry.inSpace(props.spaceId);
  const troubled = vaults.filter((vault) => vault.status !== "unlocked").length;
  return [
    {
      id: "space",
      label: t.nav.space,
      items: [
        { path: "general", label: t.nav.general, icon: "settings" },
        { path: "access", label: t.nav.access, icon: "users" },
        { path: "sharing", label: t.nav.sharing, icon: "link" },
        { path: "templates", label: t.nav.templates, icon: "page" },
        ...(projector.value ? [{ path: "sets", label: t.nav.sets, icon: "book-open" }] : []),
        { path: "trash", label: t.nav.trash, icon: "trash" },
      ],
    },
    {
      id: "knowledge",
      label: t.nav.knowledge,
      items: [
        // Only while the AI is available (AI-24); the readers of the space never see it.
        ...(spaceAiAvailable.value ? [{ path: "ai", label: t.nav.ai, icon: "ai" }] : []),
        { path: "search-gaps", label: t.nav.searchGaps, icon: "search" },
        // Ověřený obsah (5.2): the space's content health (what is verified, what expires, what is read).
        { path: "content-health", label: tv.health.nav, icon: "shield-check" },
      ],
    },
    {
      id: "vaults",
      label: t.nav.vaultsGroup,
      items: [
        {
          path: "vaults",
          label: t.nav.vaults,
          icon: "lock",
          count: registry.loaded.value ? troubled || vaults.length : undefined,
          tone: troubled ? "warning" : undefined,
          countLabel: troubled ? t.nav.needsAttention : t.nav.vaultsCount,
        },
        (() => {
          const pending = requests?.inSpace(props.spaceId).length ?? 0;
          return { path: "requests", label: t.nav.requests, icon: "user-check", count: pending || undefined, tone: pending ? ("warning" as const) : undefined, countLabel: t.nav.awaiting };
        })(),
      ],
    },
  ];
});

const segments = computed(() => router.route.value.segments);
// The pages swap with the shared page hooks (pageSwap.ts): the new one starts at the top, the old one fades where the reader saw it.
const swap = usePageSwap(() => router.route.value.path);

/** A plain click on "Správa wiki" goes through the shell, which then leads back here; the address stays for a new tab. */
function openWikiSettings(event: MouseEvent) {
  if (!context.openWikiSettings || !isPlainNavigationClick(event)) return;
  event.preventDefault();
  context.openWikiSettings();
}
</script>

<template>
  <SettingsShell
    v-if="space"
    :title="space.name"
    :eyebrow="t.nav.thisSpace"
    :subtitle="t.nav.subtitle"
    :icon="space.icon || 'book-open'"
    :groups="groups"
    :nav-label="t.nav.navLabel(space.name)"
  >
    <template #actions>
      <Button v-if="context.isWikiAdmin" variant="outline" as-child><a :href="globalSettingsHome" data-global-settings @click="openWikiSettings"><AtlasIcon name="settings" :size="16" />{{ t.nav.wikiAdmin }}</a></Button>
    </template>
    <template v-if="flash.message.value" #notice>
      <ResultBanner :key="flash.message.value.key" :message="flash.message.value.text" :icon="flash.message.value.icon" @dismiss="flash.dismiss()" />
    </template>

    <!-- A page leaves with a fade while the next one rises in over it (settings.css, st-page), as in Správa wiki. The next
         page starts at its top: scrolled down in the old one, the reader would otherwise see the new page's header above
         the fold for the length of the fade and then the scroll would jump when the old page is gone. -->
    <Transition name="st-page" @before-enter="swap.beforeEnter" @before-leave="swap.beforeLeave">
    <SpaceGeneralPage v-if="segments[0] === 'general'" key="general" :space-id="spaceId" />
    <SpaceSharingPage v-else-if="segments[0] === 'sharing'" key="sharing" :space-id="spaceId" />
    <PageTemplatesPage v-else-if="segments[0] === 'templates'" key="templates" :space="space" @select="context.openPage($event)" @create="context.newTemplate(spaceId)" />
    <SpaceSetsPage v-else-if="segments[0] === 'sets' && projector" key="sets" :space-id="spaceId" />
    <SpaceTrashCleanupPage v-else-if="segments[0] === 'trash'" key="trash" :space-id="spaceId" />
    <SpaceSearchGapsPage v-else-if="segments[0] === 'search-gaps'" key="search-gaps" :space-id="spaceId" />
    <SpaceContentHealthPage v-else-if="segments[0] === 'content-health'" key="content-health" :space-id="spaceId" />
    <SpaceAiPage v-else-if="segments[0] === 'ai' && spaceAiAvailable" key="ai" :space-id="spaceId" :ai="aiAvailable" :mcp="context.mcpEnabled === true"><DuplicatePagesSection :space-id="spaceId" /></SpaceAiPage>
    <SpaceAccessPage v-else-if="segments[0] === 'access'" :key="`access-${segments.slice(1).join('/')}`" :space-id="spaceId" :part="segments.slice(1)" />
    <AddVaultPage v-else-if="segments[0] === 'vaults' && segments[1] === 'new'" key="vault-new" :space-id="spaceId" back-path="vaults" />
    <VaultDetailPage v-else-if="segments[0] === 'vaults' && segments[1]" :key="`vault-${segments[1]}`" :space-id="spaceId" :vault-id="segments[1]" :tab="segments[2] ?? 'status'" />
    <SpaceVaultsPage v-else-if="segments[0] === 'vaults'" key="vaults" :space-id="spaceId" />
    <AccessRequestsPage v-else-if="segments[0] === 'requests'" key="requests" :space-id="spaceId" />
    <EmptyState v-else icon="compass" :title="t.missing.title" :text="t.missing.text">
      <Button as-child variant="outline"><a :href="router.href('general')"><AtlasIcon name="back" :size="16" />{{ t.nav.generalSettings }}</a></Button>
    </EmptyState>
    </Transition>
  </SettingsShell>
  <EmptyState v-else icon="book-open" :title="t.nav.notFound" :text="t.nav.notFoundText" />
</template>
