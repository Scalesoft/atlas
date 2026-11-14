<script setup lang="ts">
import { computed, ref } from "vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import { createRelatedPeople } from "../../../assist/relatedPeople";
import { rankPeople, type PersonHit } from "../../../contacts/contactSearch";
import PageConnections from "../../PageConnections.vue";
import SettingsPage from "../../settings/SettingsPage.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import type { LinkingPage, PageConnectionsApi } from "../../../pageConnections";

/**
 * "Odkazuje sem" (R2) and "Podobné tématem" (R1), the footer of a published page. Local data, no server. Odkazuje sem is a rule and
 * Podobné tématem is embedding similarity, not model output, so neither has an AI glyph, twinkle or shimmer; motion is the prototype's
 * "Čtení stránky", moment 5 (the nodes rise one by one, the related tags 90 ms later).
 */
const replay = ref(0);

const pages: LinkingPage[] = [
  { id: "onboarding-it", spaceId: "it", spaceName: "IT a infrastruktura", title: "Onboarding IT" },
  { id: "prace-z-domova", spaceId: "hr", spaceName: "Lidé a HR", title: "Práce z domova" },
  { id: "bezpecnostni-zasady", spaceId: "it", spaceName: "IT a infrastruktura", title: "Bezpečnostní zásady" },
  ...Array.from({ length: 11 }, (_, index): LinkingPage => ({ id: `postup-${index + 1}`, spaceId: index % 3 ? "it" : "ops", spaceName: index % 3 ? "IT a infrastruktura" : "Provoz", title: `Provozní postup ${index + 1}` })),
];
const similar: LinkingPage[] = [
  { id: "certifikaty-zarizeni", spaceId: "it", spaceName: "IT a infrastruktura", title: "Certifikáty zařízení" },
  { id: "wifi-pro-hosty", spaceId: "ops", spaceName: "Provoz", title: "Wi-Fi pro hosty" },
  { id: "vzdalena-plocha", spaceId: "it", spaceName: "IT a infrastruktura", title: "Vzdálená plocha" },
];
const local = (count: number, related?: LinkingPage[]): PageConnectionsApi => ({
  relations: async () => null,
  get: async (_pageId, limit) => ({ linksHere: { items: pages.slice(0, Math.min(count, limit)), total: count }, ...(related ? { related: { items: related } } : {}) }),
});
const graphBoth = local(3, similar.slice(1));
const listBoth = local(3, similar);
const relatedOnly = local(0, similar);
const many = local(pages.length);
const empty: PageConnectionsApi = { get: async () => ({ linksHere: { items: [], total: 0 } }), relations: async () => null };
const failing: PageConnectionsApi = { get: async () => { throw new Error("Server neodpovídá."); }, relations: async () => null };
const graphDocument: MarkdownRichValue = { format: "atlas-rich", version: 1, document: { type: "doc", content: [
  { type: "paragraph", content: [{ type: "pageReference", attrs: { id: "certifikaty-zarizeni" } }] },
] } };
/** The text of the list demo links to a page that also links here (Vzájemné odkazy) and to one that does not (Odkaz z textu). */
const listDocument: MarkdownRichValue = { format: "atlas-rich", version: 1, document: { type: "doc", content: [
  { type: "paragraph", content: [{ type: "pageReference", attrs: { id: "onboarding-it" } }, { type: "pageReference", attrs: { id: "certifikaty-zarizeni" } }] },
] } };
/** P15 boost: the `@` list for „jan“ ranked as editorDirectories ranks it, with a local answer of the related pages instead of the server. */
const aiOn = ref(true);
const relatedPeople = createRelatedPeople({ get: async () => ({ linksHere: { items: [], total: 0 }, related: { items: similar, people: ["jan-dvorak"] } }) }, () => aiOn.value);
const namedJan: PersonHit[] = [["jan-novak", "Jan Novák", "Provoz"], ["jan-dvorak", "Jan Dvořák", "IT a infrastruktura"], ["petr-janda", "Petr Janda", "Lidé a HR"]]
  .map(([objectId, name, department]) => ({ key: `obj:${objectId}`, objectId, contact: { name: name!, department }, source: "directory", origin: "Adresář" }));
const mentionList = computed(() => rankPeople("jan", namedJan, relatedPeople("vpn")));
const graphSource = { resolve: async (id: string) => {
  const page = [...pages, ...similar].find(item => item.id === id);
  return page ? { id, title: page.title, url: `#/pages/${id}` } : null;
} };
</script>

<template>
  <SettingsPage title="Odkazuje sem" description="Patička čtené stránky: kdo na ni odkazuje a co je jí tématem blízké. Bez modelu, proto bez ikony AI. Živá komponenta s místními daty.">
    <SettingsSection title="Pavouk souvislostí" description="Zobrazuje se pod obsahem publikované stránky při čtení, nikdy v editaci, na veřejném sdílení ani v náhledu historie. Varianta graph komponenty PageConnections pro procházení skutečných odkazů. Výchozí je jediný řádek malých tagů a odkazu na mapu, bez nadpisů. Na úzké obrazovce se zalomí. Mapu si čtenář rozbalí až na vyžádání; pak zabere celou šířku patičky, ukáže všechny vazby najednou bez stránkování a bez vazeb se nezobrazí. Směr vazby je uveden u každého uzlu; vzájemné odkazy se sloučí. Podobné stránky doplňují stejný řádek tagů, rozlišené ikonou a tooltipem; v mapě se za skutečné vazby nevydávají. Hierarchie stromu se za vazbu nepovažuje. Celé názvy se zalamují, Tab prochází běžné odkazy a v úzkém okně se graf složí pod sebe. Při prvním vykreslení uzly a tagy vystoupají po jedné, Podobné tématem o 90 ms později; při obnově po publikaci se nic nepřehrává a při omezeném pohybu zbyde jen prolnutí. Použijte graf pro objevování návazných stránek; sloupec „Odtud dál“ téže komponenty (layout list) je v další sekci a v panelu stránky, viz příklad „Stránka: boční panel stránky“.">
      <div class="st-actions"><Button variant="outline" @click="replay++">Přehrát od začátku</Button></div>
      <p class="connections-demo-label">Příchozí, odchozí a podobné stránky v jednom řádku; mapa obsahuje pouze skutečné vazby. Resolver je místní, galerie nevolá backend.</p>
      <PageConnections :key="`graph-${replay}`" layout="graph" page-id="vpn" page-title="Připojení k firemní VPN včetně certifikátu zařízení" space-id="it" :api="graphBoth" :document="graphDocument" :page-source="graphSource" />
      <p class="connections-demo-label">Více vazeb: všechny uzly najednou, navigace klávesnicí i myší</p>
      <PageConnections :key="`graph-many-${replay}`" layout="graph" page-id="vpn" page-title="Připojení k VPN" space-id="it" :api="many" />
      <p class="connections-demo-label">Stránka bez dostupných odkazů</p>
      <PageConnections :key="`graph-empty-${replay}`" layout="graph" page-id="vpn" page-title="Nová stránka" :api="empty" />
      <p class="connections-demo-label">Jen podobné stránky: nic na stránku neodkazuje a v textu není odkaz, zůstane řádek tagů bez mapy</p>
      <PageConnections :key="`graph-related-${replay}`" layout="graph" page-id="vpn" page-title="Nová stránka" space-id="it" :api="relatedOnly" />
      <p class="connections-demo-label">Výpadek embeddingů nebo vypnutá AI: related-enabled=false okamžitě skryje podobné stránky, skutečné vazby zůstanou; po obnovení schopnosti se načtou čerstvé</p>
      <PageConnections :key="`graph-outage-${replay}`" layout="graph" page-id="vpn" page-title="Připojení k firemní VPN včetně certifikátu zařízení" space-id="it" :api="graphBoth" :document="graphDocument" :page-source="graphSource" :related-enabled="false" />
      <p class="connections-demo-label">Chyba serveru: nic se nevykreslí, patička nenechá stránku poskočit</p>
      <PageConnections :key="`graph-failing-${replay}`" layout="graph" page-id="vpn" page-title="Nová stránka" :api="failing" />
    </SettingsSection>
    <SettingsSection title="Odtud dál (layout list)" description="Stejná komponenta PageConnections jako jeden sloupec klidných karet ve vedlejším panelu stránky; u každé stránky je řečeno, jak souvisí: Odkaz z textu, Odkazuje sem, Vzájemné odkazy, Podobné tématem. Nejdřív šest, další na vyžádání. Sloupec je úzký, jako v panelu. Živý panel je v příkladu „Stránka: boční panel stránky“.">
      <div style="max-width: 340px">
        <p class="connections-demo-label">Nic neodkazuje a tato stránka nemá odkazy: věta místo prázdné plochy</p>
        <PageConnections :key="`list-empty-${replay}`" layout="list" page-id="vpn" space-id="it" :api="empty" />
        <p class="connections-demo-label">Čtrnáct odkazů: prvních šest a „Zobrazit další (8)“</p>
        <PageConnections :key="`list-many-${replay}`" layout="list" page-id="vpn" space-id="it" :api="many" />
        <p class="connections-demo-label">Každá karta říká, jak souvisí: Odkaz z textu, Odkazuje sem, Vzájemné odkazy a Podobné tématem; název prostoru jen u stránek z jiného prostoru. Resolver je místní.</p>
        <PageConnections :key="`list-kinds-${replay}`" layout="list" page-id="vpn" space-id="it" :api="listBoth" :document="listDocument" :page-source="graphSource" />
        <p class="connections-demo-label">Jen podobné stránky: nic na stránku neodkazuje</p>
        <PageConnections :key="`list-related-${replay}`" layout="list" page-id="vpn" space-id="it" :api="relatedOnly" />
        <p class="connections-demo-label">related-enabled=false: podobné stránky zmizí, skutečné vazby zůstanou</p>
        <PageConnections :key="`list-outage-${replay}`" layout="list" page-id="vpn" space-id="it" :api="listBoth" :document="listDocument" :page-source="graphSource" :related-enabled="false" />
      </div>
    </SettingsSection>
    <SettingsSection title="Lidé z podobných stránek" description="assist/relatedPeople.ts, žádná nová komponenta: s AI (schopnost related) má v nabídce @ a v nabídce jména přednost ten, koho jmenují stránky tématem podobné té upravované (embeddingy indexu; prohlížeč neposílá text a prostor vyloučený z AI podobné stránky nemá). Mění jen pořadí mezi stejně dobrými shodami: nikdy nepředběhne lepší shodu jména, nikdy nepropojí sám a nic nového nezobrazí. Odpověď se nečeká, první seznam je bez ní; nabídka jména se přeřadí, jen dokud v ní uživatel nepohnul výběrem. Bez AI zůstane pořadí podle shody jména. Místní data, galerie nevolá backend.">
      <label class="related-people-switch"><Switch v-model="aiOn" />AI k dispozici</label>
      <p class="connections-demo-label">Nabídka @ pro „jan“</p>
      <ol class="related-people-demo" data-related-people-demo>
        <li v-for="person in mentionList" :key="person.key">{{ person.contact.name }} · {{ person.contact.department }}</li>
      </ol>
    </SettingsSection>
    <SettingsSection title="Proč nová komponenta" description="Nejbližší je řádek knihovny (.library-item v PersonalLibraryPanel): velký řádek se jménem prostoru, časem a stavem pro seznam stránek čtenáře. Patička potřebuje malé tagy a rozbalitelnou mapu souvislostí, kde Podobné tématem (tagy s přímým popisem podobnosti) přibývá jen na pokyn serveru, a stejný jazyk pohybu jako zbytek návrhů 2.0. Volte PersonalLibraryPanel pro seznam stránek jako samostatný pohled, PageConnections pro souvislosti pod čteným dokumentem a v panelu stránky." />
  </SettingsPage>
</template>

<style scoped>
.connections-demo-label { margin: 18px 0 0; font-size: 12px; color: var(--muted); }
.related-people-switch { display: inline-flex; align-items: center; gap: 8px; }
.related-people-demo { margin: 6px 0 0; padding-left: 20px; }
</style>
