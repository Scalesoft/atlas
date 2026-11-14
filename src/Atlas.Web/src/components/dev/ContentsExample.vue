<script setup lang="ts">
import { computed, ref } from "vue";
import { memoryRelations, memoryUsage, type MemoryObject } from "../../objects/memoryRelations";
import type { RelationEdge } from "../../objects/relationsApi";
import { useWorkspacePanel } from "../../composables/useWorkspacePanel";
import SpacePageList from "../SpacePageList.vue";
import UnlinkedMentions from "../linking/UnlinkedMentions.vue";
import { ApiError } from "../../api";
import type { UnlinkedApi, UnlinkedMention } from "../../linking/unlinkedApi";
import UsagePanel from "../objects/UsagePanel.vue";
import ConfirmDialog from "../settings/ConfirmDialog.vue";
import { Button } from "../ui/button";
import WorkspaceSidePanel from "../WorkspaceSidePanel.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import type { TreePage } from "../../treeMove";

// The real list, dock and graph over objects held in memory (objects/memoryRelations.ts answers as the server does): nothing leaves the gallery.
const object = (id: string, typeId: string, title: string, extra: Partial<MemoryObject> = {}): MemoryObject =>
  ({ id, typeId, title, spaceId: "provoz", spaceName: "Provoz", readable: true, ...extra });
const objects: MemoryObject[] = [
  object("11111111-1111-4111-8111-111111111111", "atlas.page", "Provozní příručka", { path: "Příručky", comments: 3, updatedAt: "2026-10-26T09:30:00Z" }),
  object("22222222-2222-4222-8222-222222222222", "atlas.page", "Postup **vydání**", { path: "Příručky › Vydání", updatedAt: "2026-10-25T14:00:00Z" }),
  object("33333333-3333-4333-8333-333333333333", "atlas.page", "Nasazení 2.15", { path: "Vydání", comments: 1, updatedAt: "2026-10-27T08:15:00Z" }),
  object("44444444-4444-4444-8444-444444444444", "atlas.page", "Bezpečnostní zásady", { path: "Příručky", updatedAt: "2026-09-02T10:00:00Z" }),
  object("55555555-5555-4555-8555-555555555555", "atlas.page", "Zapomenutá poznámka", { path: "Archiv", updatedAt: "2026-03-14T10:00:00Z" }),
  object("66666666-6666-4666-8666-666666666666", "atlas.board", "Plánování vydání", { path: "Vydání", updatedAt: "2026-10-24T16:45:00Z" }),
  object("77777777-7777-4777-8777-777777777777", "atlas.board", "Tabule nasazení", { path: "Nasazení 2.15", hostId: "33333333-3333-4333-8333-333333333333", updatedAt: "2026-10-27T08:20:00Z" }),
  object("88888888-8888-4888-8888-888888888888", "atlas.contact", "Jana Nováková", { updatedAt: "2026-10-01T10:00:00Z" }),
  object("99999999-9999-4999-8999-999999999999", "atlas.contact", "Petr Král", { scope: "wiki", spaceName: "Celá wiki", updatedAt: "2026-08-12T10:00:00Z" }),
  object("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "atlas.board", "Tabule jiného týmu", { readable: false, spaceName: "Finance", spaceId: "finance" }),
];
const [handbook, procedure, deployment, security, forgotten, planning, board, jana, petr, hidden] = objects.map(item => item.id);
const edge = (sourceId: string, targetId: string, kind: RelationEdge["kind"], count = 1): RelationEdge => ({ sourceId, targetId, kind, count });
const edges: RelationEdge[] = [
  edge(handbook!, procedure!, "link"), edge(procedure!, handbook!, "link"), edge(deployment!, procedure!, "link"), edge(deployment!, security!, "link"),
  edge(handbook!, planning!, "placement", 2), edge(procedure!, planning!, "placement"), edge(jana!, planning!, "placement"), edge(handbook!, hidden!, "placement"),
  edge(board!, deployment!, "attached"), edge(deployment!, jana!, "mention"), edge(security!, "5a5a5a5a-5a5a-45a5-85a5-5a5a5a5a5a5a", "link"),
];
const relations = memoryRelations(objects, edges, { pageSize: 6 });
const usage = memoryUsage(objects, edges);

// „Použito na“ of one contact across spaces: kde, jak, proč, a draft's use and a place the reader may not see.
const people: MemoryObject[] = [
  object(jana!, "atlas.contact", "Jana Nováková"),
  object(handbook!, "atlas.page", "Provozní příručka"),
  object(planning!, "atlas.board", "Plánování vydání"),
  object(security!, "atlas.page", "Bezpečnostní zásady"),
  object("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", "atlas.page", "Rozpočet 2027", { spaceId: "finance", spaceName: "Finance" }),
  object("cccccccc-cccc-4ccc-8ccc-cccccccccccc", "atlas.page", "", { readable: false, spaceId: "hr", spaceName: "HR" }),
];
const used = (sourceId: string, kind: RelationEdge["kind"], extra: Partial<RelationEdge> = {}): RelationEdge =>
  kind === "placement" ? { sourceId: jana!, targetId: sourceId, kind, count: 1, ...extra } : { sourceId, targetId: jana!, kind, count: 1, ...extra };
const janaUsage = memoryUsage(people, [
  used(handbook!, "mention", { excerpt: "Za vydání a jeho schválení odpovídá Jana Nováková." }),
  used(handbook!, "reference", { partId: "node-1" }),
  used(planning!, "placement", { count: 2, partId: "card-7" }),
  used(security!, "mention", { draft: true, excerpt: "Nově hlásí incidenty Jana Nováková." }),
  used("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", "mention", { excerpt: "Schvaluje: Jana Nováková" }),
  used("cccccccc-cccc-4ccc-8ccc-cccccccccccc", "mention"),
]);
const opened = ref("");
function openPlace(id: string, typeId: string, partId: string | null) {
  opened.value = `Otevře se ${typeId === "atlas.board" ? "tabule" : "stránka"} ${id}${partId ? ` na ${partId}` : ""}.`;
}
const pages = objects.filter(item => item.typeId === "atlas.page").map<TreePage>((item, index) => ({ id: item.id, spaceId: "provoz", parentId: null, title: item.title ?? "", version: 2, isPublished: index !== 4 }));

// The dock as the application mounts it: one panel, filled by the list.
const panel = useWorkspacePanel();
const tabs = computed(() => panel.tabs.value);
const open = computed({ get: () => panel.open.value, set: value => { panel.open.value = value; } });
const trashAsked = ref(false);

// Nepropojené zmínky over sample rows in memory: „Petr Král“ on Porady answers 409 once (the page changed meanwhile), the rest is a second page.
const mention = (pageId: string, pageTitle: string, ordinal: number, text: string, context: string, kind: UnlinkedMention["kind"], candidates: UnlinkedMention["candidates"], state: UnlinkedMention["state"] = "suggested"): UnlinkedMention =>
  ({ pageId, pageTitle, version: 3, ordinal, text, anchor: null, context, kind, state, candidates });
const unlinkedRows = [
  mention(handbook!, "Provozní příručka", 0, "Jana Nováková", "Za provoz odpovídá Jana Nováková, zástupce je Petr Král.", "person",
    [{ id: jana!, label: "Jana Nováková", hint: "Modelsoft" }, { id: "jn-2", label: "Jana Nováková", hint: "Nemocnice Na Kopečku" }], "unresolved"),
  mention(handbook!, "Provozní příručka", 0, "Postup vydání", "Před nasazením projděte Postup vydání a kontrolní seznam.", "page", [{ id: procedure!, label: "Postup vydání", hint: "Příručky › Vydání" }]),
  mention(deployment!, "Porady", 0, "Petr Král", "Termín potvrdí Petr Král do pátku.", "person", [{ id: petr!, label: "Petr Král", hint: "Celá wiki" }]),
  mention(deployment!, "Porady", 0, "PX2-1235", "Chyba přihlášení se řeší v PX2-1235.", "issue", [{ id: "PX2-1235", label: "PX2-1235", hint: "Přihlášení padá po obnově hesla" }]),
];
let conflicted = false;
const unlinked: UnlinkedApi = {
  async list(_space, query) {
    if (query?.pageId) return { items: unlinkedRows.filter(row => row.pageId === query.pageId), nextCursor: null };
    return query?.cursor ? { items: unlinkedRows.slice(3), nextCursor: null } : { items: unlinkedRows.slice(0, 3), nextCursor: "dalsi" };
  },
  async resolve(row) {
    if (row.text === "Petr Král" && !conflicted) { conflicted = true; throw new ApiError(409, "Stránka se změnila."); }
    const at = unlinkedRows.findIndex(other => other.pageId === row.pageId && other.text === row.text);
    if (at >= 0) unlinkedRows.splice(at, 1);
    return { version: row.version + 1 };
  },
};
</script>

<template>
  <SettingsPage title="Obsah prostoru" description="Záložka Obsah (SpacePageList se spaceId) nad ukázkovými objekty v paměti. Hledání a tlačítka v lištách volají stejnou smlouvu jako server (GET /api/spaces/{id}/contents a /api/objects/{id}/relations), jen odpovídá objects/memoryRelations.ts.">
    <SettingsSection title="Seznam, dlaždice a dok"
      description="Obsah nahrazuje záložku Stránky. Ta znala jen stránky a tabule ze stromu, takže v ní nebylo vidět, co nikde není použité (kontakt na žádné tabuli), co nikdo neodkazuje ani odkaz do koše; a „Kde je použito“ v detailu karty znalo jen tabuli, na které karta leží. Obsah je tentýž SpacePageList (druhý seznam nevzniká) s dlaždicemi po druzích a příznacích (klik filtruje, druhý klik filtr zruší), filtrem Vztah, sloupci Použito · Odkazy · Komentáře · Změněno a řádky, které jde přetáhnout (do stromu jako odkazový uzel, na tabuli jako kartu). Hledání předvybere první řádek, Enter ho otevře, šipky ↑/↓ výběr mění a Esc hledání smaže. Klik na název otevře objekt, klik jinam do řádku jeho dok: Přehled, Vazby se seznamem po druzích a malým grafem, Použito na (UsagePanel, viz níže), Diskuse a Historie. Počet ve sloupci Použito (nebo Alt+↓ na názvu) rozbalí „Použito na“ přímo pod řádkem, Alt+↑ ho sbalí; dlaždice a filtr Nepoužité počítají i místa, která čtenář nevidí, stejně jako úklid sirotků. V úzkém prostoru jsou z řádků karty a dok překryje obrazovku. Použijte ho pro přehled všeho v jednom prostoru; seznam vlastních věcí přes prostory zůstává Knihovna.">
      <div class="contents-example-stage">
        <div class="contents-example-list">
          <SpacePageList space-id="provoz" :pages="pages" :can-write="false" :relations="relations" :usage="usage" />
        </div>
        <WorkspaceSidePanel v-model:open="open" :label="panel.label.value" :tabs="tabs" :active-tab="panel.activeTab.value" :min-width="360" :default-width="400" :auto-focus="panel.autoFocus.value"
          @update:active-tab="panel.activeTab.value = $event">
          <component :is="panel.content.value" v-if="panel.content.value" v-bind="panel.contentProps.value" />
        </WorkspaceSidePanel>
      </div>
    </SettingsSection>
    <SettingsSection title="Nepropojené zmínky (UnlinkedMentions)"
      description="Záložka prostoru Nepropojené zmínky (P15 „viditelné mezery“, GET /api/spaces/{id}/unlinked): jména, názvy stránek a klíče úkolů YouTrack, které stránky prostoru zmiňují bez odkazu, po stránkách, každá ve své větě s nejpravděpodobnějším kandidátem („nejspíš X · nebo Y“ jako nerozhodnutý čip). Záložka se ukáže jen pisateli prostoru a jen když je co propojit, s počtem jako Obsah. Jedno potvrzení na řádek (POST /api/pages/{id}/references): Enter nebo Propojit propojí prvního kandidáta, Jiný… nabídne ostatní (sdílená ActionMenu, první řádek rozsvícený), Nechat textem nechá text: nerozhodnutý čip se ve stránce změní na svůj text, zmínka prostým textem jen zmizí z tohoto přehledu a příště se nabídne znovu (zda je něco propojené, říká jen stránka, nic se neukládá mimo ni); všechno i z „Co s tím jde dělat“ řádku (RowActionsMenu). Řádek zmizí hned a klávesnice skočí na další, šipky ↑/↓ chodí po řádcích. Když se stránka mezitím změnila (409), načtou se její zmínky znovu a oznámení to řekne; zkuste Petra Krále. Po 50 řádcích Načíst další. Proč ne ContactDuplicates: ten páruje záznamy adresáře, tady se potvrzuje místo ve stránce; a proč ne podtržení v editoru: přehled najde mezery celého prostoru najednou, bez otevírání stránek.">
      <UnlinkedMentions space-id="provoz" :api="unlinked" @open="openPlace($event, 'atlas.page', null)" />
      <p class="st-muted" role="status">{{ opened || "Enter na řádku propojí, šipky přejdou na další." }}</p>
    </SettingsSection>
    <SettingsSection title="Graf vazeb"
      description="Přepínač Seznam / Vazby ukáže okolí vybraného objektu (bez výběru prvního řádku) jako BoardCanvas jen ke čtení nad odvozenými prvky: karta za uzel, spojnice za vazbu, barva a popisek podle druhu (odkazuje na, je na tabuli, patří ke stránce, zmiňuje), rozbitý odkaz je červená přerušovaná čára k uzlu „Nedostupné“ a nečitelný uzel v grafu vůbec není. Rozložení po vrstvách je čistá funkce layoutLayers (objects/layoutLayers.ts, bez cyklů i pro cyklické vazby, široká vrstva se zalomí). Najetí zvýrazní okolí (se Shiftem celý řetězec), dva vybrané uzly (Shift+klik) cestu mezi nimi, klik uzlu ukáže jeho dok, dvojklik nebo Enter objekt otevře, šipky chodí po uzlech, Esc vrátí fokus na přepínač Vazby; přepínač 1 / 2 kroky okolí rozšíří a nové uzly se zjeví od vybraného, odcházející zmizí. Nad 150 uzlů se vše mimo vybraný objekt a jeho přímé sousedy sbalí do skupin podle větve stromu, které se rozbalí klikem na jejich název. Při omezeném pohybu se uzly jen prolnou. Graf nevznikl jako samostatná komponenta: tabule už umí všechno, co potřebuje, a vlastní graf by byl druhým modulem téhož (zadání vlastníka, 27. 10.).">
      <p class="st-muted">Vyzkoušejte Vazby na „Postup vydání“: odkazuje na něj i z něj, leží na tabuli, a „Bezpečnostní zásady“ mají rozbitý odkaz.</p>
    </SettingsSection>
    <SettingsSection title="Použito na (UsagePanel)"
      description="Jeden panel pro „co kde jak a proč máme použité“ (plán 3.1, U1) u kontaktu, tabule, stránky, šablony i katalogového požadavku, nad jednou službou serveru (GET /api/usage/{id}, MCP atlas_get_relations a atlas_list_usage). Místa jsou po prostorech a v nich po stránkách a tabulích; u každého jak (zmínka, odkaz, karta na tabuli, odkaz ve stromu, tabule stránky, stránka ze šablony; „koncept“, když ho drží jen rozpracovaná verze) a proč (úryvek bloku, kde je). Místa, která čtenář nesmí vidět, jsou jen počet, nikdy název. ↑ ↓ Home End chodí po místech, Enter nebo mezerník místo otevře (s kartou nebo uzlem, o který jde), místo jde přetáhnout. Hned se ukáže kostra velikosti seznamu, po 50 místech pokračuje „Načíst další místa“. Šablona ukáže stránky, které z ní vznikly (Nastavení prostoru › Šablony stránek a dok šablony). Přílohy panel nemají: patří vždy jedné stránce, takže jejich „použití“ je ta stránka. Počet Použito v Obsahu a v atlas_list_usage je týž součet míst jako tady (i odkazy, koncepty a skrytá místa).">
      <UsagePanel :object-id="jana!" :api="janaUsage" @open="openPlace" />
      <p class="st-muted" role="status">{{ opened || "Vyberte místo šipkami a Enterem." }}</p>
    </SettingsSection>
    <SettingsSection title="Použito na: sbalené v informacích o stránce"
      description="collapsible: jeden řádek „Použito na N místech“ z levného počtu (GET /api/usage/{id}/count, týž počet čte i varování před smazáním posledního použití), seznam se načte až na rozbalení. Tak ho ukazuje dok stránky pod údaji o verzi.">
      <div class="contents-example-narrow"><UsagePanel :object-id="jana!" :api="janaUsage" collapsible /></div>
    </SettingsSection>
    <SettingsSection title="Proč UsagePanel"
      description="Nahrazuje režim usage komponenty ObjectRelations, který měl dok Obsahu, karta tabule a potvrzení koše tabule a kontaktu: znal jen publikované vazby, neřekl proč ani koncept a nečitelné místo ukazoval jako zámek místo počtu. ObjectRelations zůstává pro Vazby (co s čím souvisí oběma směry, s grafem), PageConnections pro souvislosti pod čtenou stránkou a ContactOverview pro přehled osob (kontakt z adresáře v něm ukazuje tento panel, jen osoba bez kontaktu výskyty podle identity). Volte UsagePanel, kdykoli jde o otázku „kde je tohle použité“ u jednoho objektu." />
    <SettingsSection title="Potvrzení koše: kde je použito"
      description="Potvrzení přesunu do koše (stránka, tabule, kontakt) vypíše pod textem UsagePanel se still: tatáž místa jako Použito na v doku, jen ke čtení (bez otevření, fokusu a přetažení), aby dialog nikam neodváděl. Koš odkazy nemaže: karty, odkazy a uzly stromu zůstanou a ukážou cíl jako nedostupný, po obnově zase ožijí. Nikde nepoužitý objekt ukáže krátké Nikde nepoužito.">
      <Button variant="outline" type="button" @click="trashAsked = true">Přesunout „Postup vydání“ do koše…</Button>
      <ConfirmDialog :open="trashAsked" title="Přesunout stránku do koše?" confirm-label="Přesunout do koše" confirm-icon="trash" @confirm="trashAsked = false" @cancel="trashAsked = false">
        <p><strong>Postup vydání</strong> se skryje čtenářům i vyhledávání. Správce ji může obnovit včetně uložených verzí.</p>
        <UsagePanel :object-id="procedure!" still :api="usage" />
      </ConfirmDialog>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.contents-example-stage { display: flex; min-width: 0; border-radius: var(--atlas-radius-lg); overflow: hidden; border: 1px dashed var(--line); }
.contents-example-narrow { max-width: 360px; }
.contents-example-list { flex: 1; min-width: 0; padding: 12px; container: workspace-content / inline-size; }
/* The demo dock sits in the box instead of the viewport (in the app it is sticky at 100dvh). */
.contents-example-stage :deep(.workspace-panel:not(.is-overlay)) { position: relative; height: auto; align-self: stretch; }
</style>
