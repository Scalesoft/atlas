<script setup lang="ts">
import { ref } from "vue";
import type { ChangeFilter, HomeApi, RecentChange } from "../home/homeApi";
import type { LibraryItem } from "../../personalLibrary";
import { unreadNotifications } from "../../notifications";
import HomeDashboard from "../home/HomeDashboard.vue";
import DashboardCard from "../home/DashboardCard.vue";
import ChangeRow from "../home/ChangeRow.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import OptionPills from "../settings/OptionPills.vue";

// Local sample data only: the dashboard reads through an in-memory HomeApi; nothing leaves the gallery.
const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();
const change = (id: string, title: string, space: string, author: string, minutes: number, extra: Partial<RecentChange> = {}): RecentChange => ({
  pageId: `page-${id}`, title, spaceId: "space", spaceName: space, revisionId: `revision-${id}`, version: 4, at: minutesAgo(minutes), authorId: `author-${author}`,
  authorName: author, channel: "web", isAi: false, apiToken: null, furtherChanges: 0, canOpenHistory: true, ...extra,
});
const changes: RecentChange[] = [
  change("1", "Provozní příručka", "Provoz", "Jana Nováková", 4, { channel: "mcp", isAi: true, apiToken: { id: "t1", name: "Claude Code – notebook", revoked: false }, furtherChanges: 2 }),
  change("2", "Plán nasazení 4.0", "Vývoj", "Petr Svoboda", 38, { furtherChanges: 5 }),
  change("3", "Ceník služeb 2027", "Obchod", "Karel Novák", 95, { channel: "api", isAi: true }),
  change("4", "Zápis z porady týmu", "Vývoj", "Jana Nováková", 60 * 5, { channel: "assistant", isAi: true, canOpenHistory: false }),
  change("5", "Architektura Atlasu s velmi dlouhým názvem stránky, který se nevejde na řádek", "Vývoj", "Petr Svoboda", 60 * 26, { isAi: true }),
  change("6", "Onboarding nových kolegů", "Lidé", "Eva Malá", 60 * 24 * 4, { canOpenHistory: false }),
];
const page = (id: string, title: string, space: string, minutes: number, type: "page" | "board" = "page"): LibraryItem => ({
  id, type, spaceId: "space", spaceName: space, title, parentId: null, version: 3, isPublished: true, isFavorite: false, visitedAt: minutesAgo(minutes), publishedAt: null,
});
const visits = [page("v1", "Provozní příručka", "Provoz", 2), page("v2", "Roadmapa 2027", "Vývoj", 50, "board"), page("v3", "Ceník služeb 2027", "Obchod", 180),
  page("v4", "Zápis z porady týmu", "Vývoj", 60 * 20), page("v5", "Onboarding nových kolegů", "Lidé", 60 * 24 * 3)];
const drafts = [page("d1", "Návrh smlouvy SLA", "Obchod", 0), page("d2", "Postup obnovy ze zálohy", "Provoz", 0)];
const filtered = (filter: ChangeFilter) => filter === "ai" ? changes.filter(x => x.isAi) : filter === "mine" ? changes.slice(1, 3) : changes;
const never = () => new Promise<never>(() => {});
const failed = async () => { throw new Error("Server neodpověděl (ukázka)."); };
const apis: Record<string, HomeApi> = {
  data: { changes: async filter => ({ items: filtered(filter), next: null }), recent: async () => ({ items: visits, hasMore: false, nextOffset: null }),
    drafts: async () => ({ items: drafts, hasMore: false, nextOffset: null }) },
  empty: { changes: async () => ({ items: [], next: null }), recent: async () => ({ items: [], hasMore: false, nextOffset: null }),
    drafts: async () => ({ items: [], hasMore: false, nextOffset: null }) },
  loading: { changes: never, recent: never, drafts: never },
  error: { changes: failed, recent: failed, drafts: failed },
};
const state = ref("data");
const states = [{ value: "data", label: "Data" }, { value: "empty", label: "Prázdné" }, { value: "loading", label: "Načítání" }, { value: "error", label: "Chyba" }];
unreadNotifications.value = 3;
</script>

<template>
  <SettingsPage title="Domů" description="Přehled po přihlášení (4.0): karty z malého registru (components/home/dashboardCards.ts), každá se svými daty a stavy.">
    <!-- flush: a card inside a card would lose its frame (settings.css), and these show cards. -->
    <SettingsSection title="HomeDashboard" flush
      description="home/HomeDashboard.vue na #/ (logo, kořen drobečkové navigace). Karta = id, název, ikona, komponenta, volitelně „Zobrazit vše“ (seznam Knihovny) a modul, bez kterého se nezobrazí; další karta je jeden soubor a jeden řádek registru. Žádné přeskupování ani nastavení rozvržení. Na široké ploše Co se změnilo vlevo, ostatní vpravo; pod 860 px vlastní šířky (container query, ne viewport) jeden sloupec. Každá karta si drží výšku skeletonu, takže se nic neposune, když data dorazí; skeleton se ukáže až po --atlas-motion-defer. Z klávesnice: Tab vede po filtrech a řádcích, šipky ↑/↓ po řádcích seznamu, Enter otevře stránku.">
      <OptionPills v-model="state" label="Stav ukázky" :options="states" />
      <div class="home-example-frame"><HomeDashboard :key="state" :api="apis[state]" /></div>
    </SettingsSection>
    <SettingsSection title="DashboardCard" flush
      description="home/DashboardCard.vue: rám karty Domů = SettingsSection s ikonou, #actions (filtr) a „Zobrazit vše“, plus stavy každé karty — první načtení (LoadingSkeleton s výškou rows × rowHeight), chyba s Zkusit znovu (ErrorState), prázdno (EmptyState compact). Tělo drží vyhrazenou výšku ve všech stavech. Použijte ho jen pro karty Domů; samotný seznam nebo nastavení je SettingsSection, souhrnné číslo StatTile.">
      <div class="gallery-grid home-example-cards">
        <DashboardCard title="Načítání" icon="recent" loading :rows="3" />
        <DashboardCard title="Prázdné" icon="recent" empty empty-title="Zatím jste nic neotevřeli" empty-text="Stránky, které otevřete, tu najdete znovu." :rows="3" />
        <DashboardCard title="Chyba" icon="recent" error="Server neodpověděl (ukázka)." :rows="3" />
      </div>
    </SettingsSection>
    <SettingsSection title="ChangeRow"
      description="home/ChangeRow.vue: jedna stránka v Co se změnilo — název, prostor, kdo a kdy (relativně, přesný čas v title) a čím: člověk (jen jméno), změna s AI slovem „AI“ jako v historii stránky (nikdy AiGlyph) a kanál přes ChannelBadge (MCP, API, asistent; název tokenu jen vlastníkovi a správcům wiki), „+N dalších změn“ v okně 30 dní. Řádek otevře stránku, „Historie“ (jen kdo smí stránku upravovat) otevře tuto verzi v historii stránky. Proti řádku Knihovny (PersonalLibraryPanel: stránky bez autora) nese autora a způsob změny; pod 520 px šířky řádku zůstane u Historie jen ikona.">
      <ul class="home-example-rows">
        <li v-for="item in changes" :key="item.pageId"><ChangeRow :change="item" /></li>
      </ul>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.home-example-frame { margin-top: 14px; border: 1px dashed var(--line); border-radius: var(--radius-lg); overflow: hidden; background: var(--canvas); }
.home-example-cards { grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); }
.home-example-rows { list-style: none; margin: 0 calc(var(--st-pad-x, 18px) * -1); padding: 0; }
.home-example-rows li + li { border-top: 1px solid var(--st-line-soft, var(--line)); }
</style>
