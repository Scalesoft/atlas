<script setup lang="ts">
import { memoryUsage } from "../../../objects/memoryRelations";
import { computed, ref } from "vue";
import WorkspaceSidePanel from "../../WorkspaceSidePanel.vue";
import PageSidePanel, { type PageSideTab } from "../../PageSidePanel.vue";
import PageOutline from "../../PageOutline.vue";
import PageMobileBar from "../../PageMobileBar.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { Button } from "../../ui/button";
import type { PagePublication, PagePublicationApi } from "../../../publishing/pagePublication";
import type { PageHistory } from "../../../history";
import type { PageAccess } from "../../../sharing";
import type { PageConnections, PageConnectionsApi } from "../../../pageConnections";
import "../../../projector/requirements.css";

// Refresh 2.5, board 03: the page dock beside the article. Local data only; nothing is fetched.
const article = ref<HTMLElement | null>(null);
const open = ref(false);
const phoneLocked = ref(true);
const stageWidth = ref('1200');
const tab = ref<PageSideTab | "assistant">("outline");
const tabs = [{ id: "outline", label: "Obsah" }, { id: "search", label: "Hledat" }, { id: "assistant", label: "Asistent", icon: "ai" }, { id: "history", label: "Historie" }, { id: "access", label: "Přístup" }];
const picked = ref("");
const at = (days: number) => new Date(Date.UTC(2026, 8, 23 - days, 8, 30)).toISOString();
const history = {
  list: async (): Promise<PageHistory> => ({ pageId: "demo", currentVersion: 4, nextBeforeVersion: null, items: [4, 3, 2, 1].map((version) => ({
    id: `r${version}`, version, title: "Frontend development", authorId: "u", authorName: version % 2 ? "Jana Nováková" : "Petr Král", activeFrom: at(4 - version), activeTo: null,
    isCurrent: version === 4, isPublished: version === 3, lastPublishedAt: null, changeSummary: version === 3 ? "Doplněna sekce o Composition API" : null,
  })) }),
};
const sharing = {
  pageAccess: async (): Promise<PageAccess> => ({ spaceId: "s", spaceName: "Modelsoft wiki", isPublished: true, canManage: true, items: [], hasMore: false,
    myAccess: {} as PageAccess["myAccess"], mode: "inherit", counts: { manage: 2, edit: 9, read: 31, hidden: 0, loseOnReset: 0, downgradeOnReset: 0, defaultGrants: 0, losing: [] },
    groups: [{ groupId: "g1", name: "Vývoj", memberCount: 12, role: "Writer", sources: [] }, { groupId: "g2", name: "Podpora", memberCount: 8, role: "Reader", sources: [] }], shareLinks: { active: 0, nextExpiry: null } }),
};
const connections: PageConnectionsApi = {
  relations: async () => null,
  get: async (): Promise<PageConnections> => ({
    linksHere: { items: [{ id: "ci", spaceId: "s", spaceName: "Modelsoft wiki", title: "Continuous integration" }], total: 1 },
    related: { items: [{ id: "ts", spaceId: "s", spaceName: "Modelsoft wiki", title: "TypeScript coding conventions and documentation" }, { id: "css", spaceId: "s", spaceName: "Modelsoft wiki", title: "CSS coding conventions" }] },
  }),
};
// „Použito na“ in page info: the demo page is linked from one page and shown on one board.
const usage = memoryUsage([{ id: "demo", typeId: "atlas.page", title: "Frontend development", spaceId: "s", spaceName: "Vývoj", readable: true },
  { id: "p1", typeId: "atlas.page", title: "Onboarding vývojáře", spaceId: "s", spaceName: "Vývoj", readable: true },
  { id: "b1", typeId: "atlas.board", title: "Plán Q4", spaceId: "s", spaceName: "Vývoj", readable: true }],
  [{ sourceId: "p1", targetId: "demo", kind: "link", count: 1, excerpt: "Začněte stránkou Frontend development." }, { sourceId: "demo", targetId: "b1", kind: "placement", count: 1 }]);
// Publikace in „O stránce“: a local state; switch the choice to see Ručně, Automaticky (last published automatically), Živě, or the space default.
const publicationState = ref<PagePublication>({ mode: null, spaceMode: "Manual", effective: "Manual", publishedVersion: 3, publishedAt: at(0), publishedBy: "Jana Nováková", publishedAutomatically: false, unpublishedChanges: true, canChange: true });
const publicationApi: PagePublicationApi = {
  get: async () => publicationState.value,
  set: async (_, mode) => (publicationState.value = { ...publicationState.value, mode, effective: mode ?? publicationState.value.spaceMode, publishedAutomatically: mode === "Automatic" || mode === "Live" }),
};
const sideTab = computed<PageSideTab>(() => (tab.value === "assistant" ? "outline" : tab.value));
</script>

<template>
  <SettingsSection title="Stránka: boční panel stránky" description="WorkspaceSidePanel se záložkami (tabs, activeTab) je dok vedle stránky: Obsah, Hledat, Asistent, Historie, Přístup. Obsah, Hledat, Historii a Přístup kreslí PageSidePanel; každá záložka je souhrn s jednou cestou na úplnou plochu (Historie stránky, Přístup ke stránce), nic se tím nepřesouvá pryč. Asistent je obsah asistenta ve stejném panelu. Obsah končí řádkem Publikace (Podle prostoru / Ručně / Automaticky po 30 minutách bez úprav / Živě; režim řekne jednou: volba tomu, kdo ho smí měnit, čtenář vidí jen stav), aby jeho pozdější odpověď nic nad sebou neposunula; „Odtud dál“ drží místo kostrou načítání. Hledat (PageSearch, 3.5 J; na stránce Ctrl F) hledá ve vykreslené stránce při čtení i úpravách, bez ohledu na velikost písmen a diakritiku, i ve sbalených sekcích a skrytých poznámkách požadavků: všechny shody jemně, aktuální výrazně (CSS Custom Highlight API, DOM ani dokument se nemění), seznam s úryvkem a místem (nadpis › požadavek). První výsledek je předvybraný, Enter na něj skočí a dál, Shift Enter zpět, ↑ ↓ seznamem, F3, Esc do stránky na výsledek; skok doskroluje na střed a jednou pulzne. Proč nový komponent: editorové Najít a nahradit je popover nad editorem jen při úpravách (dekorace pro každou shodu, bez náhledů), SearchField jen filtruje seznam; PageSearch SearchField a SearchHighlight skládá a nahrazování nechává editoru (tlačítko Nahradit v editoru…). Zkuste v ukázce hledat „certifikát“: sbalený požadavek se při skoku rozbalí.">
    <div class="dock-controls">
      <Button variant="outline" :aria-expanded="open" @click="open = !open">{{ open ? 'Zavřít panel ukázky' : 'Otevřít panel ukázky' }}</Button>
      <label>Šířka pracovní plochy <select v-model="stageWidth" aria-label="Šířka pracovní plochy"><option value="2200">Velmi široká · 2200 px</option><option value="1200">Široká · 1200 px</option><option value="960">Notebook · 960 px</option><option value="800">Úzká · 800 px</option></select></label>
    </div>
    <p class="dock-note">Panel ponechá stránce alespoň 600 px. Když pro sloupec nezbývá místo, otevře se přes pravý okraj; pod 760 px přes obrazovku. Na velmi široké ploše stojí vždy u pravého okraje okna, ne u sloupce stránky. Otevření a zavření změní rozvržení jednou a stránka se na nové místo přesune transformací, takže se dlouhý text nepřepočítává v každém snímku. Zkuste rychle zavřít a znovu otevřít, změnit šířku nebo zapnout omezený pohyb.</p>
    <div class="dock-scroller">
    <div class="dock-stage" :style="{ maxWidth: `${stageWidth}px`, minWidth: stageWidth === '2200' ? '2200px' : undefined }">
      <article ref="article" class="dock-article">
        <h4>Frontend development</h4>
        <div class="ProseMirror">
          <h2>Vue.js</h2><p>Pro vývoj nové funkcionality využíváme Vue.js 3.</p>
          <h3>Závislosti NPM</h3><p>Balíčky se instalují přes pnpm.</p>
          <h2>Komunikace s API</h2><p>Klient se generuje z OpenAPI.</p>
          <h2>Razor + TypeScript</h2><p>Starší stránky zůstávají v Razoru.</p>
          <h2>Přístupy</h2>
          <!-- A folded requirement block (projector/requirementCollapse.ts): its note is hidden until the search jumps into it. -->
          <div class="atlas-requirement-view is-collapsed" data-requirement-view data-requirement-id="gallery-page-search" data-requirement-title="Přístup do VPN">
            <div class="requirement-content"><p>Přístup do VPN</p><p>Certifikát klienta vydává správa sítě a platí rok; o nový certifikát požádejte týden předem.</p></div>
          </div>
        </div>
        <PageOutline :root="article" variant="disclosure" />
      </article>
      <WorkspaceSidePanel v-model:open="open" label="Boční panel stránky" :tabs="tabs" :active-tab="tab" :min-width="392" :default-width="392" @update:active-tab="tab = $event as PageSideTab">
        <p v-if="tab === 'assistant'" class="dock-note">Sem patří obsah asistenta (AssistantPanel); jeho hlavička se zobrazí pod záložkami.</p>
        <PageSidePanel v-else :tab="sideTab" page-id="demo" space-id="s" page-title="Frontend development" :document-root="article" :version="4" :is-published="true" :published-version="3"
          :updated-at="at(0)" :history="history" :sharing="sharing" :connections="connections" :usage="usage" :publication-api="publicationApi" @open-place="picked = 'Otevře se místo, kde je stránka použitá.'" @open-history="revision => picked = revision ? `Otevře se Historie stránky přímo na verzi ${revision.version}.` : 'Otevře se Historie stránky (obnovení, porovnání, PDF verze).'" @open-access="picked = 'Otevře se Přístup ke stránce (#/pages/<id>/access).'" />
      </WorkspaceSidePanel>
    </div>
    </div>
    <p class="dock-note" role="status">{{ picked || "Řádek „Na této stránce“ pod stránkou je tatáž osnova pro úzké okno a pro zavřený dok (PageOutline variant=disclosure)." }}</p>
    <div class="dock-phone">
      <PageMobileBar can-ask lockable :locked="phoneLocked" :favorite="false" @read="picked = 'Číst: zpět na začátek stránky.'" @ask="picked = 'Asistent'" @favorite="picked = 'Oblíbené'" @lock="phoneLocked = !phoneLocked; picked = phoneLocked ? 'Zamčeno: stránka se jen čte.' : 'Odemčeno: editor se připojí.'" />
    </div>
    <p class="dock-note">PageMobileBar (board 13) se v aplikaci kreslí jen pod 681 px; místo Upravit nese zámek úprav (dotyk otevírá stránky zamčené); shromažďuje akce, které existují i v horní liště a v nabídce ⋯. Kdy co: WorkspaceSidePanel bez záložek pro samostatný panel (asistent mimo stránku), se záložkami pro dok stránky; AppDialog size="side" pro úlohu, která potřebuje celou pozornost (Historie stránky).</p>
  </SettingsSection>
</template>

<style scoped>
.dock-stage { display: flex; min-height: 420px; border: 1px solid var(--line); border-radius: var(--atlas-radius-lg); overflow: hidden; background: var(--surface); }
.dock-controls { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; }
.dock-controls label { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.dock-controls select { max-width: 220px; }
.dock-scroller { overflow-x: auto; }
.dock-article { flex: 1; min-width: 0; padding: 16px 20px; display: grid; align-content: start; gap: 12px; }
/* As in the application the text is a centred column: opening the panel moves it, and on the widest stage the panel joins it. */
.dock-article > * { width: 100%; max-width: 560px; justify-self: center; }
.dock-article h4 { margin: 0; font-size: 20px; }
.dock-article .ProseMirror h2 { margin: 12px 0 4px; font-size: 16px; }
.dock-article .ProseMirror h3 { margin: 8px 0 4px; font-size: 14px; }
.dock-article .ProseMirror p { margin: 0; color: var(--muted); font-size: 13px; }
.dock-note { margin: 8px 0 0; color: var(--muted); font-size: 13px; }
/* The demo column sits in the box instead of the viewport (in the app it is sticky under the top bar). */
.dock-stage :deep(.workspace-panel:not(.is-overlay)) { position: relative; top: 0; height: auto; align-self: stretch; }
.dock-phone { max-width: 390px; margin-top: 12px; border: 1px solid var(--line); border-radius: 12px; overflow: hidden; }
.dock-phone :deep(.page-mobile-bar) { position: static; display: grid; grid-auto-columns: minmax(0, 1fr); grid-auto-flow: column; gap: 4px; height: 64px; padding: 4px 8px 8px; box-sizing: content-box; background: var(--surface); }
</style>
