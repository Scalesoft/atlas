<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from "vue";
import type { BoardBlockNode } from "../../boards/boardApi";
import type { PickedObject } from "../../boards/boardBlockNodes";
import type { ObjectApi } from "../../objects/objectApi";
import { scanPaste } from "../../boards/boardPaste";
import type { YouTrackIssueFetcher } from "../../composables/useYouTrackSearch";
import PageCard from "../boards/cards/PageCard.vue";
import BoardContactPicker from "../boards/pickers/BoardContactPicker.vue";
import BoardIssuePicker from "../boards/pickers/BoardIssuePicker.vue";
import type { ContactSearch } from "../../contacts/contactHits";
import type { ContactAsk } from "../../contacts/contactLinking";
import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import { Button } from "../ui/button";
import { Switch } from "../ui/switch";
import { Textarea } from "../ui/textarea";

// Everything here is local sample data: the card is given its target, the pickers get their own search instead of the server's,
// the paste is read by the real parser. Nothing leaves the gallery.
const id = "3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b";
const page = { type: "atlas.page", resourceId: id, title: "Provozní příručka", spaceTitle: "Provoz",
  excerpt: "Tento postup popisuje, jak se u nás vydává nová verze: příprava větve, kontrola změn, nasazení na zkušební prostředí a závěrečné ověření před produkcí. Každý krok má svého vlastníka a svůj odkaz na záznam o provedení.", available: true };
// Who may edit the page (canEdit of the target) edits it from the board (Enter, F2, double-click); the card has no button for it.
const shortPage = { ...page, title: "Rychlý odkaz", spaceTitle: "Provoz", excerpt: null, canEdit: true };
const locked = { type: "atlas.page", resourceId: "a1b2c3d4-0000-4000-8000-000000000001", title: null, spaceTitle: null, excerpt: null, available: false };

// A target that arrives: a fast answer shows no placeholder, a slow one shows it after the defer and cross-fades to the content.
const slow = ref(true);
const arriving = ref<typeof page | undefined>(page);
let timer: ReturnType<typeof setTimeout> | undefined;
function reload() {
  clearTimeout(timer);
  arriving.value = undefined;
  timer = setTimeout(() => { arriving.value = page; }, slow.value ? 1100 : 90);
}
onBeforeUnmount(() => clearTimeout(timer));
const opened = ref("");

const issues = [
  { id: "2-1", key: "ATL-123", title: "Oprava přihlášení přes OIDC", url: "https://youtrack.example/issue/ATL-123", state: "In Progress" },
  { id: "2-2", key: "ATL-124", title: "Export tabule do PDF", url: "https://youtrack.example/issue/ATL-124", state: "Open" },
  { id: "2-3", key: "OPS-7", title: "Záloha tabulí do Gitu", url: "https://youtrack.example/issue/OPS-7", state: "Done" },
];
const fetchIssues: YouTrackIssueFetcher = async (query) => issues.filter(issue => `${issue.key} ${issue.title}`.toLowerCase().includes(query.toLowerCase()));
const searchContacts: ContactSearch = async () => [{ title: "Adresář partnerů", spaceName: "Obchod", widgets: [
  { kind: "contact", title: "Jana Nováková", detail: "Vedoucí provozu · Modelsoft · jana.novakova@example.cz · +420 777 123 456" },
  { kind: "contact", title: "Karel Novák", detail: "karel.novak@example.cz" },
] }];

// The directory of the contact picker: one contact in it, a page's contact is linked to it on confirmation.
const directory: Pick<ObjectApi, "list"> = {
  list: async () => ({ items: [{ id: "c-petr", typeId: "atlas.contact", scope: "wiki", spaceId: null, title: "Petr Svoboda", version: 1, updatedAt: "2026-10-26T10:00:00Z",
    data: { name: "**Petr** Svoboda", title: "Jednatel", emails: ["petr@example.cz"] } }], total: 1, page: 1, pageSize: 50, canCreate: true }),
};
// Linking without a server: Karel has a similar contact in the directory, so the panel asks (ContactMatchPanel); anyone else is added.
async function linkContact(contact: MarkdownContact, ask: ContactAsk): Promise<MarkdownContact | null> {
  const id = `c-${contact.name.toLowerCase().replace(/\W+/g, "-")}`;
  if (!contact.name.startsWith("Karel")) return { ...contact, objectId: id };
  const answer = await ask({ kind: "weak", name: contact.name, candidates: [{ contactId: "c-karel", name: "Karel Novák", contact: { name: "Karel Novák", organization: "Modelsoft" } }] });
  return answer && { ...contact, objectId: answer.contactId ?? id };
}

const picking = ref<"" | "issues" | "contacts">("");
// The pickers drop under their buttons here, as they drop from the top of the canvas on a board.
const pickerAnchor = ref<HTMLElement>();
const inserted = ref<BoardBlockNode[]>([]);
function picked(blocks: BoardBlockNode[]) { inserted.value = blocks; picking.value = ""; }
const placed = ref<PickedObject[]>([]);
function pickedContacts(objects: PickedObject[]) { placed.value = objects; picking.value = ""; }

const pasted = ref(`Podívat se na zákaznické požadavky
${location.origin}/#/pages/${id}
${location.origin}/#/pages/${id}?heading=postup
ATL-123 ATL-124
https://youtrack.example/issue/OPS-7
Sloupec A\tSloupec B`);
const scanned = computed(() => scanPaste(pasted.value, { origin: location.origin, pathname: location.pathname, youtrackBaseUrls: ["https://youtrack.example/"] }));
const label = (item: (typeof scanned.value.items)[number]) => item.kind === "page" ? `Karta stránky ${item.resourceId.slice(0, 8)}…${item.anchor ? `, sekce „${item.anchor}“` : ""}` : item.kind === "youtrack" ? `Úkol YouTrack ${item.issueId}` : item.kind === "object" ? `Karta objektu ${item.typeId}` : `Poznámka: ${item.text}`;
</script>

<template>
  <SettingsPage title="Tabule: karty a výběry" description="Karta stránky, výběr úkolů YouTrack, výběr kontaktů a čtení vloženého a přetaženého obsahu (components/boards/cards, components/boards/pickers, boards/boardPaste.ts, boards/boardDrop.ts) s ukázkovými daty.">
    <SettingsSection title="PageCard" description="Karta stránky na tabuli: ikona, název, prostor, úryvek a Otevřít. Stránku, kterou čtenář nesmí číst, ukáže jako zástupnou kartu se zámkem bez názvu a bez odkazu. Kartu kreslí uzel plátna a ona nic nenačítá: cíl jí předá session tabule (item.target), dokud cíl nepřijde, zůstane bez zástupného stavu 150 ms (--atlas-motion-defer), pak se zástupný stav prolne na obsah. Použijte ji jako zobrazení „Karta“ stránky na tabuli; živý náhled, bloky editoru (úkol YouTrack, graf) a karta objektu (kontakt adresáře, ObjectCard s pohledem typu, viz galerie Kontakty a adresář) jsou jiné komponenty. Tlačítko Upravit karta nemá (3.6, stránka nemá režim úprav): kdo smí stránku upravit (target.canEdit), otevře na tabuli Enter, F2 nebo dvojklikem na vybrané kartě editor stránky v panelu přes tabuli (BoardPageEditor ve BoardView, stejná živá relace dokumentu jako wiki, COL2-9); karta ukazuje jen úryvek, text stránky v ní není. Náhled stránky (PagePreview) se upravuje přímo v textu. Otevřít je odkaz karty a Ctrl+Enter.">
      <div class="board-cards-row">
        <div class="board-cards-node" data-example-page><PageCard :item="{ id: 'n1', targetResourceId: page.resourceId }" :target="page" @open="opened = $event" /></div>
        <div class="board-cards-node" data-example-short><PageCard :item="{ id: 'n2', targetResourceId: page.resourceId }" :target="shortPage" /></div>
        <div class="board-cards-node" data-example-locked><PageCard :item="{ id: 'n3', targetResourceId: locked.resourceId }" :target="locked" /></div>
        <div class="board-cards-node" data-example-arriving><PageCard :item="{ id: 'n4', targetResourceId: page.resourceId }" :target="arriving" /></div>
      </div>
      <div class="board-cards-controls">
        <Button variant="outline" size="sm" type="button" data-example-reload @click="reload">Načíst znovu</Button>
        <label class="board-cards-switch"><Switch v-model="slow" />Pomalá odpověď (zástupný stav se ukáže po 150 ms)</label>
      </div>
      <p class="st-muted">Čtvrtá karta: rychlá odpověď nic neukáže ani neblikne, pomalá ukáže zástupný stav a prolne na obsah. <template v-if="opened">Otevřít: {{ opened }} (jen v galerii).</template></p>
    </SettingsSection>

    <SettingsSection title="BoardIssuePicker a BoardContactPicker" description="Výběry pro vložení víc úkolů YouTrack nebo kontaktů najednou (nejvýše 100), v rámu BoardPickerPanel: lehký panel v podobě vyskakovacích oken editoru (jako zadání adresy odkazu nebo /Kontakt), bez ztmavení, spadne od horního okraje plátna doprostřed, kam karty přibudou. Esc, křížek nebo klik mimo zruší (v hledání první Esc smaže napsané a druhý panel zavře). Proč ne AppDialog: modální okno přes celou stránku zakryje tabuli, do které se vkládá. Stejný rám má BoardPagePicker (karta stránky), jehož seznam je seznam [[ editoru. Výsledky se zaškrtávají a vybrané zůstávají jako štítky i při dalším hledání. Než něco napíšete, výběr hned ukáže naposledy přidané úkoly prostoru tabule nebo naposledy přidané kontakty (nejvýše 8, do zavření karty prohlížeče; zkuste přidat a výběr otevřít znovu). Po napsání je první ještě nevybraný výsledek předvybraný: Enter ho zaškrtne a vymaže hledání pro další; Enter stisknutý, dokud hledání neodpoví, počká na odpověď a zaškrtne její první výsledek. BoardPagePicker a příkazy tabule (BoardCommandPanel, / a Ctrl+K) mají tytéž klávesy: Enter nebo Tab vezme předvybraný první řádek, první Esc smaže napsané, druhý zavře. Bez napsaného textu a bez šipky není zvýrazněný žádný řádek (a Enter nic nevezme); první šipka nebo najetí ukáže řádek, který Enter pak vezme. Úkoly jsou uzly editoru youtrackIssue (blok tabule). Kontakty jsou živé objekty adresáře (karta objektu): nejdřív kontakty adresáře, pak kontakty zapsané jen ve stránkách, které se při potvrzení propojí s adresářem (na shodu s existujícím kontaktem se zeptá panel ContactMatchPanel přímo ve výběru; zkuste Karla Nováka); „Nový kontakt“ otevře formulář pod seznamem (InlineReveal). SearchableChoiceList to nezvládne, je to radiogroup, kde šipka vybírá; SearchableTags si výsledky filtruje sám a s diakritikou, takže by zahodil odpověď serveru. Panel vstupuje pohybem vyskakovacích oken editoru, výsledky přibývají po 40 ms.">
      <div class="board-cards-controls">
        <Button type="button" data-example-issues @click="picking = 'issues'">Přidat úkoly z YouTrack…</Button>
        <Button type="button" data-example-contacts @click="picking = 'contacts'">Přidat kontakty…</Button>
      </div>
      <p ref="pickerAnchor" class="st-muted" data-example-inserted>Poslední vložení: {{ inserted.length ? inserted.map(block => block.type).join(", ") : "nic" }}</p>
      <pre v-if="inserted.length" class="board-cards-json">{{ JSON.stringify(inserted, null, 2) }}</pre>
      <BoardIssuePicker :open="picking === 'issues'" space-id="space" space-name="Provoz" :fetch-issues="fetchIssues" :anchor="pickerAnchor" @pick="picked" @cancel="picking = ''" />
      <p class="st-muted" data-example-placed>Poslední kontakty na tabuli: {{ placed.length ? placed.map(object => object.objectId).join(", ") : "nic" }}</p>
      <BoardContactPicker :open="picking === 'contacts'" :search="searchContacts" :directory="directory" :link="linkContact" :anchor="pickerAnchor" @pick="pickedContacts" @cancel="picking = ''" />
    </SettingsSection>

    <SettingsSection title="Vložení a přetažení" description="parsePaste a readDrop: adresa stránky této instance (i s odkazem na sekci), ID nebo adresa úkolu YouTrack a prostý text. Řádek jen z odkazů je karta na každý z nich, jakýkoli jiný řádek je poznámka; stránka a úkol vznikne jednou, nejvýše 100 položek, tabulátory z tabulky jsou mezery a znaky, které server odmítá, se odstraní. Úkol se před vložením přeloží na blok editoru službou hostitele (resolvePasteItems) a co se nepřeloží, zůstane poznámkou.">
      <Textarea v-model="pasted" rows="7" aria-label="Vložený text" data-example-paste />
      <ol class="board-cards-parsed" data-example-parsed>
        <li v-for="(item, index) in scanned.items" :key="index" :data-kind="item.kind">{{ label(item) }}</li>
      </ol>
      <p v-if="scanned.omitted" class="st-muted">Dalších {{ scanned.omitted }} položek se nevloží.</p>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.board-cards-row { display: flex; flex-wrap: wrap; gap: 16px; }
/* A node of the canvas as the canvas draws it: border and shadow, which answer the pointer with colour and shadow only. */
.board-cards-node { width: 280px; height: 176px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); box-shadow: var(--refresh-card-shadow); overflow: hidden; transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .board-cards-node:hover { box-shadow: var(--refresh-card-shadow-hover); } }
.board-cards-controls { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 16px; }
.board-cards-switch { display: inline-flex; align-items: center; gap: 8px; font-size: 13px; }
.board-cards-json { max-height: 220px; margin: 0; padding: 10px 12px; overflow: auto; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--hover); font-size: 12px; }
.board-cards-parsed { display: grid; gap: 4px; margin: 0; padding-left: 20px; font-size: 13px; }
.board-cards-parsed li[data-kind="page"] { color: var(--accent); }
.board-cards-parsed li[data-kind="youtrack"] { color: var(--success, var(--accent)); }
</style>
