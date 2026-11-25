<script setup lang="ts">
import { defineComponent, h } from "vue";
import SpaceContentHealthPage from "../../settings/space/SpaceContentHealthPage.vue";
import ResultBanner from "../../settings/ResultBanner.vue";
import { provideSettingsContext } from "../../settings/context";
import { createSettingsFlash, provideSettingsFlash } from "../../settings/flash";
import { provideSettingsRoute } from "../../settings/useSettingsRoute";
import SettingsPage from "../../settings/SettingsPage.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import type { ContentHealth, ContentHealthApi, ContentHealthItem, VerificationState } from "../../../verification/pageVerification";

/**
 * „Stav obsahu“ (5.2), a section of Nastavení prostoru → Znalosti. Local data, no server: a small space whose pages are filtered, sorted and
 * verified in memory. The state of a page is the server's (one C# function); the gallery only holds the answers.
 */
const day = 86_400_000, at = (days: number) => new Date(Date.now() + days * day).toISOString();
const row = (n: number, title: string, state: VerificationState, readers: number, over: Partial<ContentHealthItem> = {}): ContentHealthItem => ({
  pageId: `00000000-0000-4000-8000-0000000001${String(n).padStart(2, "0")}`, title, path: ["Provoz"], state, stewardId: "u1", stewardName: "Jana Nováková",
  verifiedUntil: state === "unverified" ? null : at(state === "expired" ? -12 : state === "expiring" ? 9 : 120), isPublished: true, canEdit: true, views: readers * 3, readers, lastViewedAt: at(-2), lastPublishedAt: at(-40 - n), ...over,
});
const pages = (): ContentHealthItem[] => [
  row(1, "Přístup VPN", "expired", 48), row(2, "Zálohování databáze", "expiring", 31, { path: ["Provoz", "Databáze"] }), row(3, "Nový přístupový formulář", "unverified", 22, { stewardId: null, stewardName: null }),
  row(4, "Provozní příručka", "verified", 64), row(5, "Postup při výpadku", "expired", 3, { stewardId: null, stewardName: null }), row(6, "Archiv poznámek z roku 2022", "unverified", 0, { path: ["Provoz", "Archiv"] }),
  // A page the reader may only read (no Ověřit, no checkbox) and one set to „Neověřovat“ (its state, nothing to verify).
  row(7, "Mzdové předpisy", "expired", 12, { canEdit: false }), row(8, "Kontakty na dodavatele", "off", 9, { verifiedUntil: null }),
];
const rank: Record<VerificationState, number> = { expired: 0, unverified: 0, expiring: 1, verified: 2, off: 3 };

function createApi(): ContentHealthApi {
  let current = pages();
  const answer = (items: ContentHealthItem[]): ContentHealth => ({ enabled: true, nextCursor: null, items, summary: {
    verified: current.filter((p) => p.state === "verified").length, expiring: current.filter((p) => p.state === "expiring").length, expired: current.filter((p) => p.state === "expired").length,
    unverified: current.filter((p) => p.state === "unverified").length, withoutSteward: current.filter((p) => !p.stewardId).length } });
  return {
    list: async (_space, query) => {
      const needle = query.q.trim().toLowerCase();
      const shown = current
        .filter((p) => (query.state === "due" ? p.state !== "verified" && p.state !== "off" : query.state === "no-steward" ? !p.stewardId : query.state === "unread" ? p.readers === 0 : true))
        .filter((p) => (query.mine ? p.stewardId === "u1" : true) && p.title.toLowerCase().includes(needle));
      const key = { priority: (p: ContentHealthItem) => rank[p.state] * 1000 - p.readers, expiry: (p: ContentHealthItem) => Date.parse(p.verifiedUntil ?? "1970-01-01"), readers: (p: ContentHealthItem) => -p.readers, published: (p: ContentHealthItem) => -Date.parse(p.lastPublishedAt ?? "1970-01-01") }[query.sort];
      return answer([...shown].sort((a, b) => key(a) - key(b)));
    },
    bulk: async (_space, request) => {
      // Without pageIds: the owner from the last author for every page of the space that has none.
      const chosen = request.pageIds ?? current.filter((p) => !p.stewardId && p.canEdit && p.state !== "off").map((p) => p.pageId);
      current = current.map((p) => !chosen.includes(p.pageId) ? p
        : request.action === "verify" ? { ...p, state: "verified" as const, verifiedUntil: at(180) }
        : { ...p, stewardId: request.action === "steward" ? request.stewardId : "u1", stewardName: request.action === "steward" ? (request.stewardId ? "Eva Nováková" : null) : "Jana Nováková" });
      return { changed: chosen.length, skipped: [] };
    },
    trash: async (_space, pageId) => {
      const before = current;
      current = current.filter((p) => p.pageId !== pageId);
      return { undo: async () => { current = before; } };
    },
  };
}
const off: ContentHealthApi = { list: async () => ({ enabled: false, nextCursor: null, items: [], summary: { verified: 0, expiring: 0, expired: 0, unverified: 0, withoutSteward: 0 } }), bulk: async () => ({ changed: 0, skipped: [] }), trash: async () => ({ undo: async () => {} }) };
const people = async () => [{ id: "u2", name: "Eva Nováková", username: "enovakova" }, { id: "u1", name: "Jana Nováková", username: "jnovakova" }];

// The settings pages read the shell's providers; the gallery gives them the same, with local callbacks.
const Shell = defineComponent({
  props: { api: { type: Object as () => ContentHealthApi, required: true } },
  setup(props) {
    const router = provideSettingsRoute("#/gallery/content-health");
    const flash = createSettingsFlash(router);
    provideSettingsFlash(flash);
    provideSettingsContext({
      userId: "u-admin", isWikiAdmin: true, spaces: [], backupWarning: null, version: undefined,
      imported: () => {}, warning: () => {}, openPage: () => {}, newTemplate: () => {}, newPage: () => {}, spaceChanged: () => {}, spaceTrashed: () => {}, spacesChanged: () => {}, accessChanged: () => {},
    });
    return () => h("div", { class: "settings-ui" }, [
      flash.message.value ? h(ResultBanner, { key: flash.message.value.key, message: flash.message.value.text, icon: flash.message.value.icon, onDismiss: () => flash.dismiss() }) : null,
      h(SpaceContentHealthPage, { spaceId: "provoz", api: props.api, people }),
    ]);
  },
});
const live = createApi();
</script>

<template>
  <SettingsPage title="Stav obsahu" description="Nastavení prostoru → Znalosti: co je ověřené, co brzy vyprší a co lidé opravdu čtou. Živá stránka s místními daty; v aplikaci ji nastavení prostoru vloží jako položku Stav obsahu.">
    <SettingsSection title="SpaceContentHealthPage" description="Čtyři dlaždice (StatTile) nastavují filtr, pod nimi jeden seznam (SettingsList) s hledáním a FilterMenu: stav, garant, řazení a období. Výchozí řazení je „Nejdřív čtené a neověřené“: nejdřív se ověřuje to, co lidé čtou. Řádek ukazuje čip stavu (stejný StatusBadge jako u stránky), garanta, čtenáře za období a poslední publikaci, bez jmen čtenářů. Klávesy: šipky procházejí seznam, mezerník vybírá, V ověří vybrané (nebo řádek), G otevře výběr garanta (první osoba předvybraná, Enter ji nastaví), Enter otevře stránku. Hromadně také Garant podle posledního autora pro první naplnění: s výběrem pro vybrané, z dlaždice Bez garanta pro celý prostor (ne jen načtené řádky); stránky, které garanta už mají, hlášení počítá zvlášť. Řádek nabízí jen to, co čtenář může: Ověřit a výběr jen tam, kde stránku smí upravovat a stránka se ověřuje (Mzdové předpisy jen čte, Kontakty na dodavatele se neověřují a ukazují to), nečtená stránka Přesunout do koše (koš stromu, Zpět v oznámení). Zkuste ověřit stránku a přepnout řazení.">
      <Shell :api="live" />
    </SettingsSection>
    <SettingsSection title="Vypnuté ověřování" description="Prostor, kde správce ověřování nezapnul, Stav obsahu v nabídce prostoru, v paletě ani v nastavení nenabízí (vypadá jako před 5.2). Kdo sem přijde adresou, uvidí vysvětlení; cestu do Obecného nastavení jen správce prostoru, editor se dozví, kdo ověřování zapíná.">
      <Shell :api="off" />
    </SettingsSection>
    <SettingsSection title="Proč bez nové komponenty" description="Je to stejný tvar jako Nenalezené dotazy a Žádosti o přístup: seznam věcí k vyřízení s akcí na řádku a filtry. Skládá hotové StatTile, SettingsList, FilterMenu, SearchField, OptionPills-like nabídky, InlineReveal pro lištu hromadných akcí a SearchableChoiceList pro garanta. Volte Stav obsahu pro údržbu obsahu podle stavu ověření a čtenosti, Nenalezené dotazy pro mezery v hledání." />
  </SettingsPage>
</template>
