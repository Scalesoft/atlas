<script setup lang="ts">
import { computed, ref, shallowRef } from "vue";
import { ApiError } from "../../api";
import type { BoardBlockNode } from "../../boards/boardApi";
import { defaultBlockNode, useEditorBlockModules, type BoardBlockHost, type EditorBlockModule } from "../../boards/editorBlocks";
import type { ChannelListener } from "../../live/liveConnection";
import BoardBlock from "../boards/blocks/BoardBlock.vue";
import PagePreview from "../boards/preview/PagePreview.vue";
import DeploymentBoardCard from "../../projector/DeploymentBoardCard.vue";
import BoardMarkdown from "../boards/text/BoardMarkdown.vue";
import BoardTextEditor from "../boards/text/BoardTextEditor.vue";
import type { TextBinding } from "../boards/text/textBinding";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import { Button } from "../ui/button";

// Local sample data only: the YouTrack host answers from a list, the page previews read a document from here and their live
// channel is a stand-in the buttons below drive. Nothing leaves the gallery.
const modules = useEditorBlockModules();
const issues = [
  { id: "2-1", key: "ATL-123", title: "Oprava přihlášení přes OIDC", url: "https://youtrack.example/issue/ATL-123", state: "In Progress" },
  { id: "2-2", key: "ATL-124", title: "Export tabule do PDF", url: "https://youtrack.example/issue/ATL-124", state: "Open" },
];
const host: BoardBlockHost = { youTrack: {
  search: async query => issues.filter(issue => `${issue.key} ${issue.title}`.toLowerCase().includes(query.toLowerCase())),
  resolve: async reference => issues.find(issue => issue.key === reference) ?? null,
} };
const blocks = ref<{ id: string; label: string; block: BoardBlockNode }[]>([
  { id: "contact", label: "Kontakt", block: { type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Jana Nováková", title: "Vedoucí provozu", organization: "Modelsoft", emails: ["jana.novakova@example.cz"], phones: ["+420 777 123 456"] }] } } } },
  { id: "chart", label: "Graf", block: defaultBlockNode(modules.find(module => module.id === "charts.insert")!)! },
  { id: "youtrack", label: "Úkol YouTrack", block: { type: "youtrackIssue", attrs: issues[0]! } },
  { id: "unknown", label: "Neznámý plugin", block: { type: "kanban", attrs: { columns: 3 }, content: [{ type: "text", text: "Sloupce K řešení, Probíhá, Hotovo" }] } },
]);
const editing = ref("");
const lastBlock = ref("");
function committed(id: string, node: BoardBlockNode) {
  blocks.value = blocks.value.map(entry => (entry.id === id ? { ...entry, block: node } : entry));
  lastBlock.value = JSON.stringify(node, null, 2);
  editing.value = "";
}
const inserting = shallowRef<EditorBlockModule>();
function inserted(node: BoardBlockNode) {
  blocks.value = [...blocks.value, { id: `new-${blocks.value.length}`, label: inserting.value?.label ?? "Komponenta", block: node }];
  lastBlock.value = JSON.stringify(node, null, 2);
  inserting.value = undefined;
}

// Texts: a note and a connector's label, each with its own binding (last writer wins, as on a board).
const note = ref("# Plán vydání\n\n**Do pátku:** změny ve větvi, _kontrola_ a [postup](#/pages/3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b).\n\n- [x] příprava\n- [ ] nasazení");
const label = ref("schvaluje **vedoucí**");
const editingText = ref<"" | "note" | "label">("");
const binding = (text: typeof note): TextBinding => ({ read: () => text.value, commit: markdown => { text.value = markdown; } });
const noteBinding = binding(note), labelBinding = binding(label);

// Page previews: the same page as a whole, one section, a section that is gone, a page the reader may not see.
const pageId = "3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b";
const version = ref(1);
const h = (level: number, headingId: string, text: string) => ({ type: "heading", attrs: { level, headingId }, content: [{ type: "text", text }] });
const p = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const pageJson = computed(() => JSON.stringify({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }], document: { type: "doc", content: [
  h(1, "prirucka", "Provozní příručka"), p(`Verze ${version.value}. Tento postup popisuje, jak se u nás vydává nová verze.`),
  h(2, "postup", "Postup"), p("Připravte větev, zkontrolujte změny a nasaďte na zkušební prostředí."), p("Po ověření nasaďte do produkce a zapište výsledek."),
  h(2, "kontakty", "Kontakty"), p("Jana Nováková, vedoucí provozu."),
] } }));
const load = async () => ({ contentJson: pageJson.value });
const refuse = async (): Promise<never> => { throw new ApiError(404, "Stránka nebyla nalezena."); };
const channels = new Map<string, Set<ChannelListener>>();
const live = {
  route: () => {},
  subscribe(name: string, listener: ChannelListener) {
    const listeners = channels.get(name) ?? new Set<ChannelListener>();
    channels.set(name, listeners.add(listener));
    return { presence: () => {}, close: () => { listeners.delete(listener); } };
  },
};
function changePage() {
  version.value++;
  for (const listener of channels.get(`page:${pageId}`) ?? []) listener.message?.("PageChanged", { pageId, kind: "working" });
}
const anchor = ref<string | null>("smazana");

// A deployment page (plan 3.4 §5) for the product and service cards: a product with its server, a core module and a category of two
// modules, a second product that needs the first one's NIA input; before them the order's own section (no module), the board's summary. „Dodat certifikát“ changes the page as a PageChanged would.
const deploymentId = "7a1c2e3f-4b5d-4c6e-8f90-a1b2c3d4e5f6";
const certificate = ref<"waiting_for_input" | "done">("waiting_for_input");
const rq = (id: string, title: string, attrs: Record<string, unknown>) => ({ type: "requirement", attrs: { id, direction: "inbound", ...attrs }, content: [p(title)] });
const deploymentJson = computed(() => JSON.stringify({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }], document: { type: "doc", content: [
  h(2, "zakazka", "Zakázka"), rq("dodatek", "Podepsaný dodatek", { state: "done", direction: "internal" }), rq("harmonogram", "Potvrzený harmonogram", { state: "in_progress", direction: "internal" }),
  h(2, "portal", "Portál pacienta"), rq("srv", "Server", { state: "done" }), rq("dns", "DNS záznam", { state: "done" }), rq("ssl", "SSL certifikát", { state: "in_progress" }),
  h(3, "zaklad", "Základ portálu"), rq("smtp", "Přístup k SMTP", { state: "waiting_for_input", party: "IT nemocnice", system: "SMTP server" }),
  h(3, "autentizace", "Autentizace"),
  h(4, "nia", "NIA"), rq("nia-cert", "Certifikát NIA", { key: "nia.cert", state: certificate.value, party: "IT nemocnice", system: "NIA" }), rq("nia-reg", "Registrace služby", { state: "planned", system: "NIA" }),
  h(4, "bankid", "BankID"), rq("bankid", "Klient BankID", { state: "blocked", system: "BankID" }),
  h(2, "mpi", "MPI"), h(3, "core", "Core"), rq("mpi-nia", "Napojení na NIA", { direction: "internal", requires: "nia.cert", state: "in_progress" }),
] } }));
const loadDeployment = async () => ({ contentJson: deploymentJson.value });
function deliverCertificate() {
  certificate.value = certificate.value === "done" ? "waiting_for_input" : "done";
  for (const listener of channels.get(`page:${deploymentId}`) ?? []) listener.message?.("PageChanged", { pageId: deploymentId, kind: "working" });
}
</script>

<template>
  <SettingsPage title="Tabule: bloky editoru, texty a náhledy" description="Editor Atlasu na tabuli (components/boards/blocks, components/boards/text, components/boards/preview, boards/editorBlocks.ts, boards/boardSections.ts) s ukázkovými daty.">
    <SettingsSection title="BoardBlock" description="Jeden blok editoru jako prvek tabule: kontakt, graf, úkol YouTrack, informační box, tabulka… Vykreslí ho staticky vlastní plugin editoru (bez editoru, takže jich je na ploše desítky), Upravit (na tabuli dvojklik) otevře malý editor jen s tímto blokem a s editací pluginu (jeho lišta a formulář), Esc nebo klik mimo ji potvrdí. Tabule nemá vlastní kontakt, graf ani úkol: jedna komponenta pro jeden účel. Neznámý plugin dostane náhradní kartu, která uložená data nemění. Na rozdíl od karet tabule (PageCard) nic nenačítá ze serveru kromě služeb hostitele (YouTrack v kontextu prostoru tabule).">
      <div class="board-editor-row">
        <div v-for="entry in blocks" :key="entry.id" class="board-editor-node" :data-example-block="entry.id">
          <BoardBlock :block="entry.block" :host="host" :editing="editing === entry.id" :aria-label="entry.label"
            @commit="committed(entry.id, $event)" @cancel="editing = ''" @remove="editing = ''" />
          <Button v-if="editing !== entry.id && entry.id !== 'unknown'" variant="outline" size="sm" type="button" class="board-editor-edit" @click="editing = entry.id">Upravit</Button>
        </div>
        <div v-if="inserting" class="board-editor-node" data-example-inserting>
          <BoardBlock :block="null" :insert="inserting" :host="host" editing @commit="inserted" @cancel="inserting = undefined" />
        </div>
      </div>
      <p class="st-muted">Komponenta (nabídka K na tabuli) čte registr lomítkové nabídky stránky; vynechané položky a důvody jsou v boardBlockExclusions.</p>
      <div class="board-editor-modules" data-example-modules>
        <Button v-for="module in modules" :key="module.id" variant="outline" size="sm" type="button" :data-module="module.id" @click="inserting = module">{{ module.label }}</Button>
      </div>
      <pre v-if="lastBlock" class="board-editor-json" data-example-last-block>{{ lastBlock }}</pre>
    </SettingsSection>

    <SettingsSection title="BoardMarkdown a BoardTextEditor" description="Texty tabule jsou Markdown jako stránky. BoardMarkdown je přečte jako stránka (i s dialektem editoru) a vykreslí statickým rendererem editoru (bez HTML ze vstupu, odkazy jen http(s), mailto a do Atlasu); BoardTextEditor je upravuje na místě editorem stránky se stejným profilem a konfigurací (lišta výběru, lomítko, Ctrl+B/I, `- `, `# ` a `[[`) a ukládá Markdown projekcí jako stránka. Esc nebo klik mimo úpravu potvrdí; na tabuli se rozepsaný text ukládá už během psaní (po pauze a při skrytí stránky), do té doby tabule hlásí „Neuloženo“. Hodnotu drží TextBinding (dnes poslední zápis vyhrává, později sdílený fragment s kurzory ostatních). Řádkový text (popisek spojnice, název rámu) zůstane na jednom řádku a Enter ho potvrdí. PageCard ukazuje prostý úryvek a DocumentEditor celou stránku s ukládáním, proto texty tabule mají vlastní dvojici.">
      <div class="board-editor-row">
        <div class="board-editor-note" data-example-note @dblclick="editingText = 'note'">
          <BoardTextEditor v-if="editingText === 'note'" :binding="noteBinding" @commit="editingText = ''" @cancel="editingText = ''" />
          <BoardMarkdown v-else :text="note" />
        </div>
        <div class="board-editor-label" data-example-label @dblclick="editingText = 'label'">
          <BoardTextEditor v-if="editingText === 'label'" :binding="labelBinding" inline @commit="editingText = ''" @cancel="editingText = ''" />
          <BoardMarkdown v-else :text="label" inline />
        </div>
      </div>
      <p class="st-muted">Dvojklik začne úpravu, Esc nebo klik mimo ji potvrdí.</p>
    </SettingsSection>

    <SettingsSection title="PagePreview" description="Živý náhled stránky pro zobrazení Náhled karty stránky: celá stránka nebo jedna sekce (nadpis po další nadpis stejné úrovně), přizpůsobená kartě, posouvatelná uvnitř a s přechodem do ztracena dole. Čte stránku jejím API (práva, reference hesel, publikovaná verze čtenáři), jen když je karta vidět, a po změně stránky (kanál page:{id}, na tabuli relace pohledu tabule, kterou poskytuje BoardView) se sama obnoví prolnutím. Na tabuli ho pisatel upravuje přímo v textu, bez tlačítka Upravit (3.6): klik do textu vybrané karty nebo dvojklik dá kurzor tam, kam mířil, Enter a F2 na začátek toho, co náhled ukazuje; teprve pak se načte živý editor stránky (BoardPageEditor inline), postaví se neviditelně nad náhledem a nahradí ho na místě (náhled sekce posune tak, aby sekce zůstala, kde byla). Esc nebo klik mimo úpravu ukončí a výběr zůstane na kartě; čtenáři a stránce jen ke čtení se nic nemění. Klik na rám nebo záhlaví kartu jen vybere, tah ji posune. Galerie editor nespouští (potřebuje server). PageCard zůstává zobrazením Karta (název a úryvek), HistoryRevisionPreview vykreslí jednu uloženou verzi bez práv a bez sekcí.">
      <div class="board-editor-row">
        <div class="board-editor-node board-editor-preview" data-example-preview-page><PagePreview :page-id="pageId" :load="load" :live="live" /></div>
        <div class="board-editor-node board-editor-preview" data-example-preview-section><PagePreview :page-id="pageId" anchor="postup" :load="load" :live="live" /></div>
        <div class="board-editor-node board-editor-preview" data-example-preview-missing><PagePreview :page-id="pageId" :anchor="anchor" :load="load" :live="live" @whole-page="anchor = null" /></div>
        <div class="board-editor-node board-editor-preview" data-example-preview-locked><PagePreview :page-id="pageId" :load="refuse" :live="live" /></div>
      </div>
      <div class="board-editor-modules">
        <Button variant="outline" size="sm" type="button" data-example-change-page @click="changePage">Změnit stránku (PageChanged)</Button>
        <Button v-if="!anchor" variant="outline" size="sm" type="button" @click="anchor = 'smazana'">Znovu cílit na smazanou sekci</Button>
      </div>
    </SettingsSection>

    <SettingsSection title="DeploymentBoardCard: Produkt a Služba" description="Karta stránky v zobrazení Produkt (product, kotva = nadpis produktu) nebo Služba (service, kotva = název služby z atributu system) ukazuje nasazení ze stránky jako diagram Projectoru: produkt se štítky infrastruktury (bloky přímo pod nadpisem produktu), kategoriemi (nadpis nad modulem, bez něj Jádro; sbalitelné) a řádkem modulu se stavem a x/y; služba se stavem, počtem a štítky svých vstupů, každý vstup jednou, i když ho používá víc produktů. Modul otevře stránku na svém nadpisu, štítek na svém požadavku, kde se zapisuje. Data čte PagePreview živě ze stránky (práva, skeleton, obnova po změně). Použijte je místo Náhledu (preview), když má tabule ukázat, kde nasazení stojí, ne text stránky; Náhled zůstává pro obsah stránky nebo sekce. První oddíl stránky bez modulů, po kterém následují produkty (požadavky zakázky samotné: dodatek, harmonogram, přístupy), není systém: jeho karta Produkt je souhrn tabule s postupem celé stránky a je podbarvená (čtvrtá ukázka). Akce Tabule nasazení skládá do tabule stránky (mimo strom prostoru, čte ji, kdo smí číst stránku) obrázek architektury: produkty vedle sebe v pořadí stránky, pod každým externí služby, které používá jako první, spojnice produkt → služba se štítkem „NIA · 2 pož.“ a spojnici mezi dvěma produkty, když system požadavku jmenuje jiný produkt stránky (napojení portálu na MPI). Karty jdou tahem přesouvat jako cokoli na tabuli; opakované složení přidá jen chybějící karty do řady pod vším, co na tabuli je, a nic nepřesune ani nezmění. Existující tabuli (třeba jeden sloupec z dřívější verze) do téhož obrázku přeskládá příkaz Uspořádat jako architekturu: u výběru více karet v nabídce Zarovnat a rozmístit a v „Co s tím jde dělat“ (pravé tlačítko, Ctrl+Mezerník), bez výběru v paletě příkazů tabule pro celou tabuli. Je to jedna změna, kterou Ctrl+Z vrátí celou; velikosti karet, vlastní poznámky, tvary a šipky nechá být a znovu přichytí jen spojnice, které tabuli složilo složení (mezi stejnými kartami a se svým štítkem). Obrázek zůstane v levém horním rohu místa, kde karty stály; kdyby tam zakryl prvek, který zůstává (poznámku, kartu jiné stránky), postaví se celý pod všechno, co zůstává. Karty více stránek na jedné tabuli se uspořádají pod sebe. Služby v řadě pod produktem stojí střídavě o krok níž, aby se štítky spojnic nepřekrývaly. Karta drží produkt nebo službu podle kotvy: po přejmenování produktu nebo služby ve stránce (nebo jeho odstranění) ukáže „Produkt už ve stránce není“ či „Služba už ve stránce není“ (třetí ukázka). Sama se nemaže, aby nezmizelo nic, co na tabuli někdo rozmístil; smažte ji ručně.">
      <div class="board-editor-row">
        <div class="board-editor-node board-editor-deployment" data-example-deployment-product><DeploymentBoardCard :page-id="deploymentId" display="product" anchor="portal" :load="loadDeployment" :live="live" /></div>
        <div class="board-editor-node board-editor-preview" data-example-deployment-service><DeploymentBoardCard :page-id="deploymentId" display="service" anchor="nia" :load="loadDeployment" :live="live" /></div>
        <div class="board-editor-node board-editor-preview" data-example-deployment-order><DeploymentBoardCard :page-id="deploymentId" display="product" anchor="zakazka" :load="loadDeployment" :live="live" /></div>
        <div class="board-editor-node board-editor-preview" data-example-deployment-missing><DeploymentBoardCard :page-id="deploymentId" display="service" anchor="mojeid" :load="loadDeployment" :live="live" /></div>
      </div>
      <div class="board-editor-modules">
        <Button variant="outline" size="sm" type="button" data-example-deliver @click="deliverCertificate">{{ certificate === "done" ? "Vrátit certifikát NIA" : "Dodat certifikát NIA" }} (PageChanged)</Button>
      </div>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.board-editor-row { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 16px; }
/* A node of the canvas as the canvas draws it. */
.board-editor-node { position: relative; display: grid; gap: 8px; width: 300px; min-height: 120px; padding: 12px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); box-shadow: var(--refresh-card-shadow); }
.board-editor-edit { justify-self: start; }
.board-editor-preview { height: 240px; padding: 0; overflow: hidden; }
.board-editor-deployment { width: 400px; height: 420px; padding: 0; overflow: hidden; }
.board-editor-note { width: 240px; min-height: 240px; padding: 14px; border-radius: 4px; background: color-mix(in srgb, var(--warning-bg) 80%, var(--surface)); box-shadow: var(--refresh-card-shadow); color: var(--text); font-size: 18px; }
.board-editor-label { align-self: center; min-width: 160px; padding: 2px 8px; border: 1px solid var(--line); border-radius: 6px; background: var(--surface); font-size: 13px; }
.board-editor-modules { display: flex; flex-wrap: wrap; gap: 8px; }
.board-editor-json { max-height: 220px; margin: 0; padding: 10px 12px; overflow: auto; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--hover); font-size: 12px; }
</style>
