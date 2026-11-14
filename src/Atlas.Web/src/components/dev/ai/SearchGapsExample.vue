<script setup lang="ts">
import { defineComponent, h, ref } from "vue";
import SpaceSearchGapsPage from "../../settings/space/SpaceSearchGapsPage.vue";
import ResultBanner from "../../settings/ResultBanner.vue";
import { provideSettingsContext } from "../../settings/context";
import { createSettingsFlash, provideSettingsFlash } from "../../settings/flash";
import { provideSettingsRoute } from "../../settings/useSettingsRoute";
import SettingsPage from "../../settings/SettingsPage.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import type { SearchGap, SearchGapsApi } from "../../../searchGaps";

/**
 * "Nenalezené dotazy" (G2), a section of Nastavení prostoru. Local data, no server. A statistic of what people searched for and did
 * not find, so it carries no AI glyph. Motion is the settings language (list, result line) and the A0 primitive collapseHeight: a resolved
 * row folds away. Under reduced motion only a fade remains.
 */
const sample: Record<number, SearchGap[]> = {
  7: [{ query: "vpn heslo", normalizedQuery: "vpn heslo", users: 5, unanswered: 3, reported: 3, lastSearchedOn: "2026-10-03" }],
  30: [
    { query: "vpn heslo", normalizedQuery: "vpn heslo", users: 5, unanswered: 3, reported: 3, lastSearchedOn: "2026-10-03" },
    { query: "dovolená", normalizedQuery: "dovolena", users: 3, unanswered: 3, lastSearchedOn: "2026-10-01" },
    { query: "zálohování databáze", normalizedQuery: "zalohovani databaze", users: 3, lastSearchedOn: "2026-09-24" },
  ],
  90: [
    { query: "vpn heslo", normalizedQuery: "vpn heslo", users: 5, unanswered: 3, reported: 3, lastSearchedOn: "2026-10-03" },
    { query: "dovolená", normalizedQuery: "dovolena", users: 3, unanswered: 3, lastSearchedOn: "2026-10-01" },
    { query: "zálohování databáze", normalizedQuery: "zalohovani databaze", users: 3, lastSearchedOn: "2026-09-24" },
    { query: "cestovní náhrady", normalizedQuery: "cestovni nahrady", users: 4, lastSearchedOn: "2026-08-12" },
  ],
};
const resolved = new Set<string>();
const populated: SearchGapsApi = {
  list: async (_space, days) => ({ items: sample[days]!.filter((gap) => !resolved.has(gap.normalizedQuery)), minimumUsers: 3, retentionDays: 90 }),
  resolve: async (_space, normalized) => { resolved.add(normalized); },
};
const empty: SearchGapsApi = { list: async () => ({ items: [], minimumUsers: 3, retentionDays: 90 }), resolve: async () => {} };
const failing: SearchGapsApi = { list: async () => { throw new Error("Server neodpovídá."); }, resolve: async () => {} };

const created = ref("");
// The settings pages read the shell's providers; the gallery gives them the same, with local callbacks.
const Shell = defineComponent({
  props: { api: { type: Object as () => SearchGapsApi, required: true } },
  setup(props) {
    const router = provideSettingsRoute("#/gallery/search-gaps");
    const flash = createSettingsFlash(router);
    provideSettingsFlash(flash);
    provideSettingsContext({
      userId: "u-admin", isWikiAdmin: true, spaces: [], backupWarning: null, version: undefined,
      imported: () => {}, warning: () => {}, openPage: () => {}, newTemplate: () => {},
      newPage: (_space, options) => { created.value = `Vytvořit stránku: otevře se zakládání stránky s názvem „${options?.title ?? ""}“ (jen v galerii).`; },
      spaceChanged: () => {}, spaceTrashed: () => {}, spacesChanged: () => {}, accessChanged: () => {},
    });
    return () => h("div", { class: "settings-ui" }, [
      flash.message.value ? h(ResultBanner, { key: flash.message.value.key, message: flash.message.value.text, icon: flash.message.value.icon, onDismiss: () => flash.dismiss() }) : null,
      h(SpaceSearchGapsPage, { spaceId: "provoz", api: props.api }),
    ]);
  },
});
</script>

<template>
  <SettingsPage title="Nenalezené dotazy" description="Statistika pro správce prostoru: co lidé hledali a nenašli. Pravidlová funkce bez modelu, proto bez ikony AI. Živá stránka s místními daty; v aplikaci ji nastavení prostoru vloží jako sekci Nenalezené dotazy.">
    <ResultBanner v-if="created" :message="created" icon="file-plus" @dismiss="created = ''" />
    <SettingsSection title="SpaceSearchGapsPage" description="Dotazy, které v prostoru skončily bez výsledku a hledali je aspoň tři různí lidé, bez jmen, za 7 / 30 / 90 dní. Řádek nabízí Vytvořit stránku (otevře zakládání s dotazem jako názvem) a Označit jako vyřešené (řádek se sklopí, nahoře se potvrdí). Zkuste přepnout období a označit řádek. S asistentem (fáze 3) přibývají u řádku důvody: Bez odpovědi asistenta (odpověď z wiki neměla podklady) a Nahlášeno (člověk nahlásil chybějící obsah), vždy jako počet různých lidí. Řádek bez důvodu vypadá jako dřív.">
      <Shell :api="populated" />
    </SettingsSection>
    <SettingsSection title="Prázdný stav a chyba" description="Pod prahem tří lidí se nic neukazuje; chyba stojí u seznamu s Zkusit znovu. Správce, kterému server odpoví 404, sekci vůbec nevidí.">
      <Shell :api="empty" />
      <Shell :api="failing" />
    </SettingsSection>
    <SettingsSection title="Proč nová stránka nastavení" description="Nejbližší je Žádosti o přístup (requests/AccessRequestsPage): seznam věcí k vyřízení s akcí na řádku. Nenalezené dotazy jsou statistika s obdobím a dvěma akcemi na řádku, proto skládají hotové SettingsList, OptionPills, EmptyState a ErrorState a nepřidávají žádný nový prvek. Volte Žádosti o přístup pro schvalování, Nenalezené dotazy pro mezery v obsahu prostoru." />
  </SettingsPage>
</template>
