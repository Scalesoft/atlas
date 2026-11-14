<script setup lang="ts">
import { defineComponent, h } from "vue";
import { ApiError } from "../../../api";
import type { SpaceAiApi } from "../../../aiPreferences";
import SpaceAiPage from "../../settings/space/SpaceAiPage.vue";
import ResultBanner from "../../settings/ResultBanner.vue";
import SettingsPage from "../../settings/SettingsPage.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { provideSettingsContext } from "../../settings/context";
import { createDirtyGuard, provideDirtyGuard } from "../../settings/dirtyGuard";
import { createSettingsFlash, provideSettingsFlash } from "../../settings/flash";
import { provideSettingsRoute } from "../../settings/useSettingsRoute";

/**
 * "AI a znalosti" (G5), a section of Nastavení prostoru for its administrator. Live page with local data, no server.
 * A switch with an impact preview: excluding the space opens "Co se změní" with what stays (ordinary search); nothing is saved
 * before Uložit. Nenalezené dotazy (G2) stays a separate section because it works without AI.
 */
function local(behaviour: "ok" | "conflict" | "hidden"): SpaceAiApi {
  let current = { included: true, version: "v1" as string | null };
  return {
    load: async () => {
      if (behaviour === "hidden") throw new ApiError(404, "Not found");
      return current;
    },
    save: async (_space, included) => {
      if (behaviour === "conflict") throw new ApiError(409, "Conflict");
      current = { included, version: "v2" };
      return current;
    },
  };
}
const Shell = defineComponent({
  props: { api: { type: Object as () => SpaceAiApi, required: true } },
  setup(props) {
    const router = provideSettingsRoute("#/gallery/space-ai");
    const flash = createSettingsFlash(router);
    provideSettingsFlash(flash);
    provideDirtyGuard(createDirtyGuard());
    provideSettingsContext({
      userId: "u-admin", isWikiAdmin: true, spaces: [], backupWarning: null, version: undefined,
      imported: () => {}, warning: () => {}, openPage: () => {}, newTemplate: () => {}, newPage: () => {}, spaceChanged: () => {}, spaceTrashed: () => {}, spacesChanged: () => {}, accessChanged: () => {},
    });
    return () => h("div", { class: "settings-ui" }, [
      flash.message.value ? h(ResultBanner, { key: flash.message.value.key, message: flash.message.value.text, icon: flash.message.value.icon, onDismiss: () => flash.dismiss() }) : null,
      h(SpaceAiPage, { spaceId: "provoz", api: props.api }),
    ]);
  },
});
const normal = local("ok");
const conflicting = local("conflict");
const hidden = local("hidden");
</script>

<template>
  <SettingsPage title="Prostor: AI a znalosti" icon="book-open" description="Přepínač Zahrnout prostor do AI s náhledem dopadu. Živá stránka s místními daty; v aplikaci ji nastavení prostoru ukáže jen správci a jen při dostupné AI.">
    <SettingsSection title="SpaceAiPage" description="Vypněte přepínač: ukáže se, co se změní a co zůstane. Uložit pošle verzi (CAS).">
      <Shell :api="normal" />
    </SettingsSection>
    <SettingsSection title="Souběh správců" description="Uložení narazí na novější verzi: změna zůstane a Načíst znovu ukáže aktuální stav.">
      <Shell :api="conflicting" />
    </SettingsSection>
    <SettingsSection title="Bez oprávnění" description="Server odpoví 404 komukoli, kdo není správcem prostoru; sekce pak neexistuje.">
      <Shell :api="hidden" />
    </SettingsSection>
  </SettingsPage>
</template>
