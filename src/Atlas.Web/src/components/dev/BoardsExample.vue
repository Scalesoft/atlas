<script setup lang="ts">
import { computed, ref, shallowRef } from "vue";
import { boardDetailTabs, type BoardDetailTab } from "../../boards/boardDetail";
import type { BoardEdit } from "../../boards/boardApi";
import { youtrackNode } from "../../boards/boardBlockNodes";
import { createBoardModel, type UndoStep } from "../../boards/boardModel";
import type { PageTargetInfo, RemoteDrag } from "../../boards/boardSession";
import BoardCanvas from "../boards/BoardCanvas.vue";
import BoardObjectDetail from "../boards/detail/BoardObjectDetail.vue";
import WorkspaceSidePanel from "../WorkspaceSidePanel.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import { Switch } from "../ui/switch";

// The real board model as the in-memory data source: every batch is confirmed at once by a pretend server (the next sequence),
// so pending, undo and redo behave as on a live board. Nothing leaves the gallery.
const model = createBoardModel({
  title: "Plánování vydání",
  sequence: 0,
  settings: { background: "dots", snapToGrid: false, gridSize: 24 },
  items: [
    { id: "f1", kind: "frame", x: -40, y: -60, width: 560, height: 360, z: 1, text: "Sprint 44", color: "blue" },
    { id: "n1", kind: "note", x: 0, y: 0, width: 200, height: 200, z: 2, text: "Co musí být hotové **před vydáním**?", color: "yellow", parent: "f1" },
    { id: "n2", kind: "note", x: 280, y: 20, width: 200, height: 200, z: 3, text: "Záloha tabulí do Gitu", color: "pink", parent: "f1" },
    // An open group around the risks, and a collapsed one standing for two finished notes (its connectors merge onto it).
    { id: "g1", kind: "group", x: 584, y: -76, width: 352, height: 252, z: 1, text: "**Rizika** vydání", color: "purple" },
    { id: "t1", kind: "text", x: 600, y: -60, width: 320, height: 56, z: 4, text: "## Rizika", props: { fontSize: "l", align: "left" }, parent: "g1" },
    { id: "s1", kind: "shape", x: 620, y: 20, width: 180, height: 140, z: 5, text: "Schválení", color: "teal", props: { shape: "diamond", fontSize: "m", align: "center" }, parent: "g1" },
    { id: "g2", kind: "group", x: 1000, y: 0, width: 452, height: 232, z: 1, text: "Hotovo", color: "green", props: { collapsed: true } },
    { id: "n3", kind: "note", x: 1016, y: 16, width: 200, height: 200, z: 8, text: "Migrace členství", color: "green", parent: "g2" },
    { id: "n4", kind: "note", x: 1236, y: 16, width: 200, height: 200, z: 9, text: "Zvýraznění okolí", color: "green", parent: "g2" },
    { id: "p1", kind: "page", x: 0, y: 380, width: 280, height: 140, z: 6, targetResourceId: "page-handbook", color: "teal", props: { display: "card" } },
    { id: "p2", kind: "page", x: 340, y: 380, width: 280, height: 140, z: 7, targetResourceId: "page-secret", props: { display: "card" } },
    { id: "c1", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: "n1", to: "n2", text: "pak", props: { route: "elbow", arrowStart: "none", arrowEnd: "arrow", dash: "solid", state: "waiting" } },
    { id: "c2", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: "n2", to: "s1", text: "**blokuje** schválení", props: { route: "curved", arrowStart: "none", arrowEnd: "arrow", dash: "dashed", state: "blocked" } },
    { id: "c3", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: "n1", to: "p1", props: { route: "elbow", arrowStart: "arrow", arrowEnd: "arrow", dash: "solid", state: "done", animated: true, thickness: "l" } },
    { id: "c4", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: "s1", to: "n3" },
    { id: "c5", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: "s1", to: "n4" },
  ],
});
const targets = new Map<string, PageTargetInfo>([
  ["page-handbook", { title: "Provozní příručka", available: true, spaceTitle: "Provoz", excerpt: "Jak se u nás vydává nová verze: příprava větve, kontrola změn a nasazení." }],
  ["page-secret", { title: null, available: false }],
]);
const samplePages = [{ id: "page-onboarding", title: "Onboarding nových kolegů" }, { id: "page-release", title: "Postup vydání" }];
let picked = 0;

const revision = ref(0);
// With the colleague on, someone also edits the handbook in its page: its card shows them as BoardView's session would.
const handbookEditors = [{ clientId: 1, id: "petr", name: "Petr Král", color: "#457b9d", cursorAvailable: false, activity: "Petr Král upravuje přes stránku Provozní příručka" }];
const items = computed(() => (revision.value, model.items.map(item => (item.targetResourceId
  ? { ...item, target: targets.get(item.targetResourceId), ...(showRemote.value && item.targetResourceId === "page-handbook" && { editors: handbookEditors }) }
  : item))));
const settings = computed(() => (revision.value, model.settings));
const canEdit = ref(true);
const showRemote = ref(false);
const remote = computed<RemoteDrag[]>(() => (showRemote.value
  ? [{ userId: "jana", displayName: "Jana Nováková", color: "#2a9d8f", cursor: { x: 470, y: 250 }, dragging: [{ itemId: "n2", x: 320, y: 60, width: 200, height: 200 }] },
    // A colleague drawing a connector: a dashed line in her colour (BoardPresence).
    { userId: "petr", displayName: "Petr Svoboda", color: "#e76f51", cursor: { x: 120, y: 420 }, dragging: [], connecting: { from: "n1", side: "bottom", x: 120, y: 420 } }]
  : []));
const lastBatch = shallowRef<readonly BoardEdit[]>([]);
const opened = ref("");
// The object detail in the dock, as BoardView opens it in the workspace.
const detailOpen = ref(false);
const detailId = ref<string | null>(null);
const detailTab = ref<BoardDetailTab>("overview");
const detailItem = computed(() => (detailId.value ? items.value.find(item => item.id === detailId.value) ?? null : null));
function openDetail(itemId: string | null) { detailId.value = itemId; detailTab.value = "overview"; detailOpen.value = true; }
// Popis shows the card's page; here a local one, so the gallery asks no server.
const preview = {
  load: async () => ({ contentJson: JSON.stringify({ format: "atlas-rich", version: 1, plugins: [], document: { type: "doc", content: [
    { type: "heading", attrs: { level: 1 }, content: [{ type: "text", text: "Provozní příručka" }] },
    { type: "paragraph", content: [{ type: "text", text: "Jak se u nás vydává nová verze: příprava větve, kontrola změn a nasazení." }] },
  ] } }) }),
  live: { route: () => {}, subscribe: () => ({ presence: () => {}, close: () => {} }) },
};

function confirm(batch: UndoStep | null) {
  if (batch) {
    model.acknowledge(batch.operationId, { sequence: model.sequence + 1, duplicate: false });
    lastBatch.value = batch.changes;
  }
  revision.value++;
}
async function pickPage() {
  const page = samplePages[picked++ % samplePages.length]!;
  targets.set(page.id, { title: page.title, available: true });
  return page;
}
// The YouTrack picker answers editor blocks; here one sample issue.
async function pickIssues() {
  const node = youtrackNode({ id: "2-17", key: "ATL-17", title: "Tabule: přichytávání do mřížky", url: "https://youtrack.example/issue/ATL-17", state: "Open" });
  return node ? [node] : [];
}
</script>

<template>
  <SettingsPage title="Tabule" description="BoardCanvas (components/boards, části v components/boards/canvas) s ukázkovými daty. Model tabule je skutečný (boards/boardModel.ts); dávky tu potvrzuje pomyslný server v paměti, nic se neukládá.">
    <SettingsSection title="BoardCanvas"
      description="Volná plocha modulu Tabule na knihovně Vue Flow v řízeném režimu: uzly a spojnice se odvozují z modelu a každé dokončené gesto odejde jako jedna dávka změn (událost change), tažení mezitím jen jako náhled pro ostatní (preview). Nástroje vlevo (V, H, N, T, S, F, L, P, K; Komponenta a K otevřou komponenty jako hledání se seznamem příkazů tabule, který po napsání názvu najde i prvek na tabuli a přenese ho do pohledu, první výsledek vybraný, Enter vloží; zvolený nástroj poznámky, textu, tvaru či rámu vloží prvek také Enterem; Alt+F10 vede do lišty výběru a Esc zpět, Tab za posledním prvkem z tabule ven), výběr tažením v prázdnu a Shift+klik, přesun celého výběru s vodítky zarovnání (Alt je vypne), změna velikosti osmi úchyty, text na místě dvojklikem, Enterem nebo F2 (Markdown jako ve stránce), spojnice tažením z úchytu, plovoucí lišta nad výběrem (kontextová lišta editoru stránky: tlačítka .bb, šipky, Home a End mezi nimi, nabídky jako kontextové nabídky editoru s ikonami v ActionMenu, protože tabule pod sebou editor nemá a openMarkdownContextPopover jej potřebuje), rámy, které nesou svůj obsah, Ctrl+C/V/D, šipky po 1 a 10 px a Ctrl+Z / Ctrl+Shift+Z. Kolečko posouvá, Ctrl+kolečko a sevření přibližují (5–400 %), Shift+1/2/0 přizpůsobí. Pozadí a přichytávání jsou nastavení tabule (ozubené kolo vpravo dole). Použijte ji pro obsah, který se rozmisťuje volně ve dvou rozměrech; seznam nebo strom s vlastním pořadím zůstává SettingsList a PageTree, dokument DocumentEditor. Žádná z nich neumí volné umístění, přiblížení ani spojnice, proto vznikla nová komponenta.">
      <div class="boards-example-controls">
        <label class="boards-example-switch"><Switch v-model="canEdit" />Úpravy povolené</label>
        <label class="boards-example-switch"><Switch v-model="showRemote" />Kolegyně na tabuli</label>
      </div>
      <div class="boards-example-stage">
        <BoardCanvas class="boards-example-canvas" :items="items" label="Plánování vydání" :can-edit="canEdit" :remote="remote" :settings="settings"
          :can-undo="model.canUndo" :can-redo="model.canRedo" :pick-page="pickPage" :pick-issues="pickIssues" :paste-context="{ origin: 'https://atlas.example' }"
          @change="(changes, coalesce) => confirm(model.apply(changes, coalesce))" @undo="confirm(model.undo())" @redo="confirm(model.redo())" @open-page="opened = targets.get($event)?.title ?? ''"
          @open-detail="openDetail" />
        <WorkspaceSidePanel v-model:open="detailOpen" :label="detailItem ? 'Detail objektu' : 'Detail tabule'" :tabs="boardDetailTabs" :active-tab="detailTab"
          :min-width="360" :default-width="400" @update:active-tab="detailTab = $event as BoardDetailTab">
          <BoardObjectDetail :item="detailItem" :items="items" board-title="Plánování vydání" :tab="detailTab" :preview="preview" @open-page="opened = targets.get($event)?.title ?? ''" />
        </WorkspaceSidePanel>
      </div>
      <p class="st-muted" data-boards-example-batch>Poslední dávka: {{ lastBatch.length ? lastBatch.map(change => change.type).join(", ") : "žádná" }}<template v-if="opened"> · Otevřít: {{ opened }} (jen v galerii)</template></p>
      <p class="st-muted"><strong>Skupiny a rámy</strong> mají členy výslovně (parentId): seskupit jde jen čtyřmi cestami: Ctrl+G, „Seskupit“ v panelu, tlačítko v liště nad výběrem dvou a více prvků a přetažení do skupiny (ven ji opustí). Ctrl+Shift+G skupinu zruší. Obrys skupiny se drží kolem členů; sbalená skupina (Hotovo) je jedna karta s počtem a spojnice jejích členů se sloučí do jedné s číslem. Každá změna je vidět (boardMotion.planGroupMotion): nový obrys se dokreslí kolem členů a ti se k sobě jemně přitáhnou, zrušený se rozplyne ven a členové se krátce zvýrazní, sbalení je vtáhne do karty, rozbalení je postupně vrátí a spojnice se znovu dokreslí, vstup a odchod obrys plynule přizpůsobí.</p>
      <p class="st-muted" data-boards-example-frames><strong>Rámy a přesuny:</strong> prvek patří do rámu nebo skupiny, kde leží jeho střed (boardGroups.arrangeChanges). Při tažení se rám, do kterého prvek dopadne, zvýrazní; když se nezvýrazní nic, prvek po puštění zůstane volně na ploše přesně tam, kam ho pustíte (víc než polovinou ven = ven z rámu). Prvek, který v rámu zůstal, ale přesahuje, rám plynule zvětší (rám se sám nikdy nezmenšuje; skupina se drží těsně kolem členů). Zmenšený rám pustí prvky, jejichž střed už nepokrývá. Přesun i se členstvím je jeden krok Zpět. Zkuste vytáhnout „Záloha tabulí do Gitu“ z rámu Sprint 44 a vrátit ji.</p>
      <p class="st-muted" data-boards-example-colours><strong>Barvy:</strong> barvu má každý druh prvku (paleta v liště, šipky mezi barvami, Enter vybere, první je „Bez barvy“). Poznámka a tvar se barvou vyplní, rám jí tónuje plochu a jméno (rám bez barvy je neutrálně šedý), karta stránky a objektu dostane jako v YouTracku barevný pruh vlevo a jemný tón; komponenta editoru nemá pruh, barva je její celé pozadí: „Pozadí“ v liště nabízí Bez pozadí, Bílou, Černou a paletu prostorů (10 rychlých barev, pod „Další barvy a tóny“ 12 odstínů × 5 tónů); text zůstává čitelný ve světlém i tmavém vzhledu. Karta požadavku na tabuli nasazení má barvu podle stavu (rozpracováno modře, čeká na vstup žlutě, čeká na prerekvizity oranžově, blokováno červeně, hotovo zeleně, plánováno šedě, netýká se bez barvy), dokud jí uživatel nevybere vlastní; „Podle stavu“ ji vrátí. Barvy jsou vidět i na minimapě a v seznamu tabule nasazení. Zkuste kartu „Provozní příručka“.</p>
      <p class="st-muted" data-boards-example-frame-rule><strong>Klik do rámu (jako ve FigJamu):</strong> klik do prázdného místa rámu nebo skupiny, na jeho okraj či název ho vybere; tažení v prázdném místě kreslí výběr (karty uvnitř zůstávají dosažitelné), ale vybraný rám se tažením kdekoli uvnitř přesune i s celým obsahem, jedním krokem Zpět a jednou dávkou. „Vybrat obsah“ v liště nebo Ctrl+A nad vybraným rámem vybere jeho členy (znovu Ctrl+A vše), Esc jde od prvku k jeho rámu a odtud ven. Dvojklik do prázdného místa vybraného rámu přidá poznámku, dvojklik na název ho přejmenuje. Nový rám nakreslený přes volné prvky si je vezme; tažená karta je nad ostatními.</p>
      <p class="st-muted" data-boards-example-connectors><strong>Spojnice:</strong> pravoúhlá spojnice vychází a vstupuje stranami, které k sobě míří, se zaoblenými rohy a obchází karty v cestě (boardRoute.routeElbow); více spojnic mezi stejnými dvěma prvky se rozestoupí a popisek leží v polovině délky. Rychlé styly v liště platí pro všechny vybrané spojnice: směr (jednosměrná, opačná, obousměrná, bez šipek), animovaný tok ve směru šipky (při omezeném pohybu stojí), čárkovaně, tloušťka a barva. Nová spojnice převezme poslední použitý styl (v tomto prohlížeči). Kreslená spojnice má od začátku svůj konečný tvar, trasu, šipky a styl (BoardConnectorPreview); nad kartou se konec přichytí ke straně, kam se připojí, a ta se označí kroužkem. Konce spojnice pod kurzorem nebo vybrané jdou chytit a přetáhnout jinam (i prstem); přepojená spojnice zůstane tou samou (stejné id, popisek, výběr), mění se jen její konce. Kolegyně, která právě kreslí spojnici, ji ukazuje přerušovaně ve své barvě. Spojnice z první poznámky do „Provozní příručky“ je obousměrná, silná a animovaná.</p>
      <p class="st-muted" data-boards-example-keys><strong>Klávesnice:</strong> Tab a Shift+Tab procházejí prvky po řádcích zleva doprava (za posledním fokus tabuli opustí), šipky posouvají výběr o 1 px, se Shiftem o 10 px, Esc vybere rám nad výběrem (a pak výběr zruší), Delete smaže s nabídkou „Zpět“, Ctrl+D duplikuje, Ctrl+G seskupí a Ctrl+Shift+G skupinu zruší. L nad jedním vybraným prvkem ho spojí s jiným: šipky vyberou cíl (zvýrazní se, čtečka ho přečte a spojnice se předkreslí), Enter spojí, Esc nechá být; prvky schované ve sbalené skupině šipky přeskočí. L nad vybranou spojnicí stejně přepojí její konec, Shift+L začátek.</p>
      <p class="st-muted" data-boards-example-rules><strong>Ovládání (pravidla P1, P2, P6, P7):</strong> první klik prvek jen vybere; úpravu kteréhokoli prvku začne Enter, F2 nebo dvojklik (text, název rámu a skupiny, popisek spojnice, komponenta, pole karty kontaktu, náhled stránky; karta stránky otevře úpravu stránky v panelu nad tabulí). Text poznámky i náhledu stránky upraví i klik do textu už vybraného prvku, s kurzorem tam, kam mířil; klik na rám nebo záhlaví prvek jen vybere. Tlačítko Upravit nemá žádný prvek. Enter nikdy nic neotevírá: kartu stránky, tabule a požadavku otevře Ctrl+Enter, její odkaz „Otevřít“ nebo „Otevřít…“ v nabídce. Pravé tlačítko (Shift+pravé nechá nabídku prohlížeče), Ctrl+mezerník, Alt+Enter, Ctrl+., Shift+F10 nebo klávesa nabídky otevře nad výběrem nabídku editoru „Co s tím jde dělat“: Upravit, Otevřít, Otevřít detail, Kopírovat odkaz a akce výběru (karta stránky, tabule nebo kontaktu se „Odebrat z tabule“, objekt zůstane; vlastní prvek tabule se „Smazat“); první řádek je předvybraný, psaní filtruje, Enter provede, Esc zavře a výběr nechá. Na odkazu a obrázku uvnitř prvku a tam, kde tabule nic nenabízí (poznámka při čtení), nechá pravé tlačítko nabídku prohlížeče. Moduly do ní přidávají řádky (boardIntentions.registerBoardIntentions; AI jen tehdy, když umí odpovědět, s Alt+Enter nahoře): Zeptat se, Vysvětlit a Shrnout nad prvkem, nad rámem či skupinou i s jejich prvky, bez výběru nad celou tabulí a v upravovaném textu nad výběrem. Úprava pole karty kontaktu je krok Zpět tabule jako každá jiná (Ctrl+Z ji vrátí přes kontakt). Ctrl+Z a Ctrl+Y (Ctrl+Shift+Z) platí i v liště úprav výběru (Alt+F10), bez návratu na prvek. Po úpravě zůstane klávesnice na prvku, i na spojnici.</p>
      <p class="st-muted"><strong>Okolí a živé hrany:</strong> najetí na uzel zvýrazní přímé vazby, nahoru modře a dolů jantarově, s klávesou Shift celý řetězec; ostatní se ztlumí. Dva vybrané uzly ukážou cestu mezi sebou. Stav spojnice (Čeká, Hotovo, Blokuje v plovoucí liště) ji obarví, otevřená a zvýrazněná nese tok ve směru vazby, který se hýbe jen u zvýrazněné, vybrané, animované nebo spojnice pod kurzorem (desítky čekajících spojnic by jinak překreslovaly tabuli v každém snímku; při omezeném pohybu stojí vždy); najetí na spojnici ukáže její typ, stav a popisek.</p>
      <p class="st-muted"><strong>Příkazový panel:</strong> lomítko nebo krátký stisk mezerníku nad prázdnou plochou ho otevře u kurzoru; Ctrl/⌘+K je hledání ve wiki. Je to seznam návrhů editoru stránky (vzhled, klávesy i pohyb lomítka) s polem hledání: vložení poznámky, textu, tvaru, rámu a skupiny, komponenty editoru z jeho registru, umístění stránky, akce nad výběrem a nastavení tabule. „Otevřít detail“ v nabídce akcí nebo „Detail“ v liště otevře <strong>detail objektu</strong> v doku pracovní plochy se záložkami Přehled, Popis, Vazby, Kde je použito, Diskuse a Historie (BoardObjectDetail ve WorkspaceSidePanel, stejný dok jako u stránek; Diskuse je CommentThread, ukázka v sekci Komentáře, a tlačítko „Diskuse“ v rámu tabule ji otevře pro tabuli).</p>
      <p class="st-muted">Karta vpravo dole ukazuje stránku, kterou čtenář nesmí číst: zástupná karta se zámkem, bez názvu a bez odkazu. Kolegyně na tabuli má kurzor se jménem a náhled svého tažení v barvě, kterou jí dává server a kterou má i její kurzor ve stránce. Kdo upravuje stránku karty jinde (ve stránce nebo na jiné tabuli), je u karty jako avatar CollaboratorAvatars s popiskem „… upravuje přes stránku …“ (nebo „jinde“, když místo čtenář vidět nesmí); přijde a odejde stejným pohybem jako avatary ve stránce. Vypnuté úpravy jsou režim jen ke čtení: zůstává posun, přiblížení, výběr, kopírování a Otevřít.</p>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.boards-example-controls { display: flex; flex-wrap: wrap; gap: 8px 20px; }
.boards-example-switch { display: inline-flex; align-items: center; gap: 8px; font-size: 13px; }
.boards-example-stage { display: flex; min-width: 0; border-radius: var(--atlas-radius-lg); overflow: hidden; }
.boards-example-canvas { flex: 1; height: 560px; }
/* The demo dock sits in the box instead of the viewport (in the app it is sticky at 100dvh). */
.boards-example-stage :deep(.workspace-panel:not(.is-overlay)) { position: relative; height: auto; align-self: stretch; }
</style>
