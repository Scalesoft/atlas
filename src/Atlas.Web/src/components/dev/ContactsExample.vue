<script setup lang="ts">
import { computed, ref } from "vue";
import { ApiError } from "../../api";
import { mergedContact, type ContactData, type ContactPatch } from "../../contacts/contactData";
import type { AccountContactSuggestion, ContactApi, ContactLinkItem, ContactLinkReport, ContactLinkRun, ContactPair } from "../../contacts/contactApi";
import { askLastUsage, type ContactAnswer, type ContactDecision } from "../../contacts/contactLinking";
import { MarkdownEditor, MarkdownRenderer } from "@atlas/markdown/vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { editorChrome, useEditorTheme } from "../../editor/editorSetup";
import { createDocumentWidgets } from "../../documentWidgets";
import { createOverviewReading } from "../../overview/overviewReading";
import { overviewKinds } from "../../overview/overviewKinds";
import ContactMatchPanel from "../contacts/ContactMatchPanel.vue";
import ContactDuplicates from "../contacts/ContactDuplicates.vue";
import ContactLinkPage from "../settings/global/ContactLinkPage.vue";
import ContactLastUsageDialog from "../contacts/ContactLastUsageDialog.vue";
import OptionPills from "../settings/OptionPills.vue";
import { provideSettingsRoute } from "../settings/useSettingsRoute";
import { Button } from "../ui/button";
import { createLiveObjects } from "../../objects/liveObjects";
import type { ObjectApi, ObjectDto, ObjectList, ObjectSummary, TrashedObject } from "../../objects/objectApi";
import { createContactsPlugin, normalizeContact, type LiveContact, type MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { rankPeople, teachesAlias, type PersonHit } from "../../contacts/contactSearch";
import ContactCard from "../contacts/ContactCard.vue";
import placeholderPhoto from "./assets/placeholder-photo.svg?no-inline";
import ContactPicker from "../contacts/ContactPicker.vue";
import ContactDirectory from "../contacts/ContactDirectory.vue";
import ContactOverview from "../contacts/ContactOverview.vue";
import PageContactEditor from "../contacts/PageContactEditor.vue";
import type { EntityApi, EntityItem } from "../../entities/entityApi";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import { Switch } from "../ui/switch";

// Local sample data only: the directory gets an in-memory object path and its own live objects, nothing leaves the gallery.
const now = "2026-10-26T10:00:00Z";
let next = 3;
const contacts = ref<ObjectSummary[]>([
  { id: "c1", typeId: "atlas.contact", scope: "wiki", spaceId: null, title: "Jana Nováková", version: 3, updatedAt: now,
    data: { name: "**Jana** Nováková", title: "Vedoucí _provozu_", organization: "Modelsoft", emails: ["jana.novakova@example.cz"], phones: ["+420 777 123 456"], website: "https://example.test", address: "Vídeňská 1\nBrno" } },
  { id: "c2", typeId: "atlas.contact", scope: "wiki", spaceId: null, title: "Karel Novák", version: 1, updatedAt: now, data: { name: "Karel Novák", department: "Podpora", emails: ["karel.novak@example.cz"], phones: ["+420 603 000 222"] } },
  // The same phone as Karel Novák: a possible duplicate (Možné duplicity).
  { id: "c3", typeId: "atlas.contact", scope: "wiki", spaceId: null, title: "Karel Novák", version: 1, updatedAt: now, data: { name: "Karel Novák", organization: "Modelsoft", phones: ["+420 603 000 222"] } },
]);
const trashed = ref<TrashedObject[]>([
  { id: "c0", typeId: "atlas.contact", scope: "wiki", spaceId: null, title: "Petr Starý", version: 2, trashedAt: "2026-10-28T15:20:00Z", trashedById: "u1", trashedByName: "Jana Nováková",
    data: { name: "Petr Starý", organization: "Dřívější dodavatel", emails: ["petr.stary@example.cz"] } },
]);
const dto = (item: ObjectSummary): ObjectDto => ({ ...item, ownerId: null, createdAt: now, createdBy: null, editingModel: "fields", canEdit: true });
const source: Pick<ObjectApi, "list" | "create" | "trash" | "trashList" | "restore" | "purge" | "get" | "change"> = {
  async list({ q }) {
    const text = (q ?? "").toLowerCase();
    const items = contacts.value.filter(item => !text || JSON.stringify(Object.values(item.data ?? {})).toLowerCase().includes(text));
    return { items, total: items.length, page: 1, pageSize: 50, canCreate: true } satisfies ObjectList;
  },
  async create(_type, _scope, data) {
    const existing = contacts.value.find(item => (item.data?.emails as string[] | undefined)?.some(email => (data.emails as string[] | undefined)?.includes(email)));
    if (existing) throw new ApiError(409, "Kontakt s touto e-mailovou adresou už v adresáři je.", "duplicate_contact", { contactId: existing.id });
    const item: ObjectSummary = { id: `c${next++}`, typeId: "atlas.contact", scope: "wiki", spaceId: null, title: String(data.name), version: 1, updatedAt: now, data };
    contacts.value = [item, ...contacts.value];
    return dto(item);
  },
  async trash(id) {
    const item = contacts.value.find(entry => entry.id === id)!;
    contacts.value = contacts.value.filter(entry => entry.id !== id);
    const { id: key, typeId, scope, spaceId, title, version, data } = item;
    trashed.value = [{ id: key, typeId, scope, spaceId, title, version, data, trashedAt: new Date().toISOString(), trashedById: "me", trashedByName: "Vy" }, ...trashed.value];
  },
  async trashList() { return { items: trashed.value, total: trashed.value.length, page: 1, pageSize: 50 }; },
  async restore(id) {
    const item = trashed.value.find(entry => entry.id === id)!;
    trashed.value = trashed.value.filter(entry => entry.id !== id);
    const { typeId, scope, spaceId, title, data } = item;
    contacts.value = [{ id, typeId, scope, spaceId, title, data, version: item.version + 1, updatedAt: new Date().toISOString() }, ...contacts.value];
    return { version: item.version + 1, duplicate: false };
  },
  async purge(id) { trashed.value = trashed.value.filter(entry => entry.id !== id); },
  async get(id) { const item = contacts.value.find(entry => entry.id === id); if (!item) throw new ApiError(404, "Objekt nebyl nalezen."); return dto(item); },
  async change(id, payload) {
    const item = contacts.value.find(entry => entry.id === id)!;
    const data = { ...item.data };
    for (const [key, value] of Object.entries(payload.fields as Record<string, unknown>)) { if (value === null) delete data[key]; else data[key] = value; }
    Object.assign(item, { data, version: item.version + 1 });
    return { version: item.version, duplicate: false };
  },
};
const store = createLiveObjects(source);

// Možné duplicity over the sample directory: Karel twice by one phone; a merge keeps one with the fields of both, the other leaves.
// Propojit účty s kontakty (a wiki admin's view): Jana's account by the same name, the account without an address (its username says
// which one it is); Eva's account whose own contact exists already (Propojit merges it); Není to on gives the account its own contact (all its pairs go) with Zpět in the toast.
const candidate = (id: string) => { const item = contacts.value.find(entry => entry.id === id); return item && { contactId: id, name: item.title, contact: item.data as unknown as ContactData }; };
const accountPairs = ref<AccountContactSuggestion[]>([
  { accountId: "u-jana", accountName: "Jana Nováková", accountEmail: null, accountUsername: "jana.novakova", accountActive: true, contact: { contactId: "c-jana", name: "Jana Nováková", contact: { name: "Jana Nováková", organization: "Modelsoft", emails: ["jana.novakova@example.cz"] } }, reason: "name" },
  { accountId: "u-eva", accountName: "Eva Malá", accountEmail: "eva@example.test", accountActive: true, contact: { contactId: "c-eva", name: "Eva Malá", contact: { name: "Eva Malá", emails: ["eva.mala@old.example.test"] } }, reason: "name", ownContactId: "c-eva-own" },
]);
const hiddenPairs: AccountContactSuggestion[] = [];
const duplicates: Pick<ContactApi, "duplicates" | "merge" | "accountSuggestions" | "resolveAccount"> = {
  async accountSuggestions() { return { items: accountPairs.value.slice() }; },
  async resolveAccount(accountId, contactId, decision) {
    if (decision === "restore") { accountPairs.value.push(...hiddenPairs.filter(pair => pair.accountId === accountId)); hiddenPairs.splice(0, hiddenPairs.length, ...hiddenPairs.filter(pair => pair.accountId !== accountId)); }
    else { const gone = accountPairs.value.filter(pair => pair.accountId === accountId); if (decision === "dismiss") hiddenPairs.push(...gone); accountPairs.value = accountPairs.value.filter(pair => !gone.includes(pair)); }
    return { mergedContactId: null, createdContactId: decision === "dismiss" ? `own-${contactId}` : null };
  },
  async duplicates() {
    const [a, b] = [candidate("c2"), candidate("c3")];
    return { pairs: a && b ? [{ a, b, reason: "phone" } satisfies ContactPair] : [] };
  },
  async merge(sourceId, targetId) {
    const from = contacts.value.find(entry => entry.id === sourceId)!, to = contacts.value.find(entry => entry.id === targetId)!;
    to.data = mergedContact(to.data as unknown as ContactData, from.data as unknown as ContactData) as unknown as Record<string, unknown>;
    to.version++;
    contacts.value = contacts.value.filter(entry => entry.id !== sourceId);
    return { contactId: targetId };
  },
};

// ContactMatchPanel: the three questions a picked person's match may ask.
const usedIn = [{ kind: "page" as const, id: "p1", title: "Provozní příručka" }, { kind: "board" as const, id: "b1", title: "Tabule provozu" }];
const decisions: Record<string, ContactDecision> = {
  exists: { kind: "exists", name: "Jana Nováková", contactId: "c1", usedIn },
  weak: { kind: "weak", name: "Karel Novák", candidates: [{ contactId: "c2", name: "Karel Novák", contact: { name: "Karel Novák", department: "Podpora", emails: ["karel.novak@example.cz"] } }] },
  conflict: { kind: "conflict", name: "Jana Nováková", contactId: "c1", usedIn, canEdit: true,
    conflicts: [{ field: "phones", existing: ["+420 777 123 456"], new: ["+420 602 000 111"] }, { field: "title", existing: "Vedoucí provozu", new: "Ředitelka provozu" }] },
};
const decisionKinds = [{ value: "exists", label: "Už v adresáři", icon: "info" }, { value: "weak", label: "Podobný", icon: "users" }, { value: "conflict", label: "Rozpor", icon: "triangle-alert" }];
const decisionKind = ref("exists");
const answered = ref("zatím nic");
const answer = (given: ContactAnswer | null) => { answered.value = given ? JSON.stringify(given) : "Zpět, nic se nevloží"; };

// The contacts plugin with a live directory (`live`): a contact the directory changed, one in the trash, one the reader may not read.
const theme = useEditorTheme();
const ids = { ok: "a1b2c3d4-0000-4000-8000-000000000011", trashed: "a1b2c3d4-0000-4000-8000-000000000012", hidden: "a1b2c3d4-0000-4000-8000-000000000013" };
const live = async (objectId: string): Promise<LiveContact> => (objectId === ids.ok
  ? { state: "ok", contact: { name: "Jana Nováková", title: "Ředitelka provozu", organization: "Modelsoft", phones: ["+420 602 000 111"], objectId } }
  : { state: objectId === ids.trashed ? "trashed" : "hidden" });
const livePlugins = [createContactsPlugin({ live })];
// One contact per block, one after another: text and headings can go between them.
const livePage: MarkdownRichValue = { format: "atlas-rich", version: 1, plugins: [{ id: "contacts", version: 2 }], document: { type: "doc", content: [
  { type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Jana Nováková", title: "Vedoucí provozu", phones: ["+420 777 123 456"], role: "PM projektu", objectId: ids.ok }] } } },
  { type: "heading", attrs: { level: 3 }, content: [{ type: "text", text: "Bývalí dodavatelé" }] },
  { type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Petr Starý", organization: "Dřívější dodavatel", objectId: ids.trashed }] } } },
  { type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Eva Malá", organization: "Example s.r.o.", objectId: ids.hidden }] } } },
] } };

// PageContactEditor over the sample directory (Jana, c1): directory fields change the sample contact, the page's own fields the snapshot on close.
const pageSnapshot: MarkdownContact = { name: "Jana Nováková", role: "PM projektu", objectId: "c1" };
const pageEditorOpen = ref(true);
const pageStored = ref("zatím nic");
const pageUsage = { count: async (objectId: string) => ({ objectId, total: 3, visible: 3, hidden: 0 }) };
const pageSave = (contact: MarkdownContact) => { pageStored.value = JSON.stringify({ role: contact.role, username: contact.username, kind: contact.kind, emails: contact.emails }); };

// K2: the question after the last use of a contact was removed (ContactLastUsageDialog, mounted once by the app).
const lastAnswer = ref("zatím nic");
const removeLast = async () => { lastAnswer.value = (await askLastUsage("Jana Nováková")) ? "Přesunout do koše" : "Ponechat jako nepoužitý"; };

// ContactOverview over sample entities: one person from the directory and two pages, one only in a page still being written.
const page = (id: string, title: string, anchor: string | null, revisionKind: "working" | "published" = "published") =>
  ({ resourceId: id, resourceType: "atlas.page", title, spaceId: "provoz", spaceName: "Provoz", anchor, revisionKind });
const entities: EntityItem[] = [
  { identityKey: "email:jana.novakova@example.cz", kind: "contact", attributes: contacts.value[0]!.data as unknown as ContactData,
    sources: [{ resourceId: "c1", resourceType: "atlas.contact", title: "Jana Nováková", spaceId: null, spaceName: null, anchor: null, revisionKind: "published" },
      page("11111111-1111-4111-8111-111111111111", "Provozní příručka", "kontakty"), page("33333333-3333-4333-8333-333333333333", "Nasazení 2.15", null)], sourceCount: 3 },
  { identityKey: "name:petr kral|example", kind: "contact", attributes: { name: "Petr Král", organization: "Example s.r.o.", phones: ["+420 602 111 222"] },
    sources: [page("22222222-2222-4222-8222-222222222222", "Postup vydání", "dodavatele", "working")], sourceCount: 1 },
  // Named in many pages: the list carries the first five places, Zobrazit všechny výskyty brings the rest (PERF2-1).
  { identityKey: "email:eva.mala@example.cz", kind: "contact", attributes: { name: "Eva Malá", organization: "Example s.r.o.", emails: ["eva.mala@example.cz"] },
    sources: Array.from({ length: 8 }, (_, i) => page(`4444444${i}-4444-4444-8444-444444444444`, `Zápis z porady ${i + 1}`, null)), sourceCount: 8 },
];
const entitySource: EntityApi = {
  async list(_kind, { q, identity }) {
    const text = (q ?? "").toLowerCase();
    const items = entities.filter(item => (identity ? item.identityKey === identity : !text || JSON.stringify(item.attributes).toLowerCase().includes(text)))
      .map(item => (identity ? item : { ...item, sources: item.sources.slice(0, 5) }));
    return { items, total: items.length, page: 1, pageSize: 50 };
  },
  exportUrl: () => "#gallery-export",
};

const editable = ref(true);
const card = ref<ContactData>({ name: "**Petr** Svoboda", title: "Jednatel", organization: "Example s.r.o.", emails: ["petr@example.cz"], phones: ["+420 602 000 111"], aliases: ["PSv", "Péťa"] });
// What the picker would teach the directory (learnAlias): shown here, nothing leaves the gallery.
const learned = ref("zatím nic");
const learnPerson = (hit: Pick<PersonHit, "contact" | "objectId">, query: string) => { learned.value = teachesAlias(query, hit) ? `„${query.trim()}“ → ${hit.contact.name}` : `nic („${query.trim()}“ je začátek jména)`; };
const changes = ref<ContactPatch[]>([]);
// „Logo nebo fotka“ (package G): the chosen file stays in the browser; the card shows a neutral placeholder as if it were uploaded.
const sampleUpload = async () => placeholderPhoto;
function changed(patch: ContactPatch) {
  changes.value = [patch, ...changes.value].slice(0, 3);
  card.value = Object.fromEntries(Object.entries({ ...card.value, ...patch }).filter(([, value]) => value !== null)) as unknown as ContactData;
}
// ContactPicker over sample people: one person in the directory and as an account (shown once), two Jan Nováks apart, a nickname.
const people: PersonHit[] = [
  { key: "user:1", contact: { name: "Honza Kovařík", emails: ["jan.kovarik@example.cz"], organization: "AB Klinika" }, source: "account", origin: "Účet Atlasu" },
  { key: "obj:2", contact: { name: "Jan Novák", organization: "Modelsoft", emails: ["jan@modelsoft.example"] }, source: "directory", origin: "Adresář" },
  { key: "page:3", contact: { name: "Jan Novák", organization: "AB Klinika" }, source: "page", origin: "Stránka Kontakty · Zakázky" },
  { key: "obj:4", contact: { name: "Josef Dvořák", title: "Správce sítě" }, source: "directory", origin: "Adresář" },
  // Two „MM“: initials find both, „MMu“ / „MMa“ one; Marek is also „Mára“, an alias learned from earlier picks.
  { key: "obj:5", contact: { name: "Marek Musil", title: "Vedoucí vývoje" }, source: "directory", objectId: "c5", origin: "Adresář", aliases: ["Mára"], aliasPicks: { mara: 3 } },
  { key: "obj:6", contact: { name: "Michal Macek", department: "Provoz" }, source: "directory", objectId: "c6", origin: "Adresář" },
];
const searchPeople = async (text: string) => rankPeople(text, people);
const picked = ref("zatím nikdo");
const pickPerson = (contact: MarkdownContact) => { picked.value = contact.name; };
const lastChange = computed(() => (changes.value.length ? JSON.stringify(changes.value[0]) : "zatím nic"));
// ContactLinkPage: a stub api per state; "running" advances on every poll and ends done.
const item = (name: string, extra: Partial<ContactLinkItem> = {}): ContactLinkItem => ({ name, identityKey: name, occurrences: 2, pageIds: ["p1", "p2"], ...extra });
const linkSample: ContactLinkReport = {
  mode: "report", pages: 214, revisions: 0,
  linked: [item("Jana Nováková"), item("Karel Novák", { pageIds: ["p3"], occurrences: 1 })], created: [item("Petr Dvořák"), item("Eva Malá"), item("Tomáš Černý")],
  unclear: [item("K. Novák", { reason: "possible_duplicate" })], restricted: [item("Lenka Nová", { reason: "restricted", pageIds: ["p9"], occurrences: 1 })],
};
const linkStates = [{ value: "report", label: "Report", icon: "file-text" }, { value: "running", label: "Běží", icon: "hourglass" }, { value: "done", label: "Hotovo", icon: "check" }, { value: "failed", label: "Selhalo", icon: "triangle-alert" }];
const linkState = ref("report");
// ContactLinkPage links the run to its detail under Úlohy na pozadí: the link needs the surface's router.
provideSettingsRoute("#/gallery");
function linkApiFor(state: string): NonNullable<InstanceType<typeof ContactLinkPage>["$props"]["api"]> {
  const run = (over: Partial<ContactLinkRun>): ContactLinkRun => ({ id: 1, state: "running", startedAt: now, pages: 214, pagesDone: 0, revisions: 0, report: linkSample, ...over });
  let done = 40;
  const advance = () => { done = Math.min(214, done + 60); return run(done >= 214 ? { state: "succeeded", pagesDone: 214, revisions: 231, finishedAt: now } : { pagesDone: done }); };
  return {
    linkReport: async () => linkSample,
    linkApply: async () => run({ pagesDone: 0 }),
    linkRun: async () => (state === "running" ? advance() : state === "done" ? run({ state: "succeeded", pagesDone: 214, revisions: 231 }) : state === "failed" ? run({ state: "failed", pagesDone: 97, revisions: 104 }) : null),
    linkCancel: async () => run({ state: "cancelled", pagesDone: done, revisions: 40 }),
  };
}
const linkApi = computed(() => linkApiFor(linkState.value));

// The page overview of kind contacts (contacts/contactOverview.ts) over a page's people: in the editor and as a reader sees it.
const person = (contact: MarkdownContact) => ({ version: 1, contacts: [contact] });
const petr: MarkdownContact = { name: "Petr Malý", organization: "Nemocnice Horní Lhota", emails: ["petr.maly@nemocnice.example"], objectId: "a1b2c3d4-0000-4000-8000-000000000021" };
const overviewDocument = { type: "doc", content: [
  { type: "overview", attrs: { id: "a1b2c3d4-0000-4000-8000-000000000020", kind: "contacts" } },
  { type: "paragraph", content: [{ type: "text", text: "Za nemocnici jedná " }, { type: "contactInline", attrs: { data: person(petr) } }, { type: "text", text: "." }] },
  { type: "contactCard", attrs: { data: { version: 1, contacts: [
    { name: "Jana Nováková", title: "Vedoucí provozu", organization: "Modelsoft", emails: ["jana@modelsoft.example"], phones: ["+420 777 123 456"], objectId: "a1b2c3d4-0000-4000-8000-000000000022" },
    { name: "Ivana Malá", title: "Primářka", organization: "Nemocnice Horní Lhota", phones: ["+420 602 000 222"], objectId: "a1b2c3d4-0000-4000-8000-000000000023" }] } } },
  { type: "contactCard", attrs: { data: person(petr) } },
] };
const overviewValue = { format: "atlas-rich", version: 1, plugins: [{ id: "contacts", version: 2 }, { id: "atlas-requirements", version: 1 }], document: overviewDocument } as unknown as MarkdownRichValue;
const overviewPage = ref<MarkdownRichValue>(structuredClone(overviewValue));
const overviewPlugins = createDocumentWidgets({ kind: "edit" });
const overviewReading = [...createDocumentWidgets({ kind: "public" }), createOverviewReading(() => "gallery-contacts")];
const kinds = overviewKinds().map(kind => `${kind.label} (${kind.kind})`).join(", ");
</script>

<template>
  <SettingsPage title="Kontakty a adresář" description="Kontakt jako objekt wiki (atlas.contact): karta kontaktu s úpravou na místě a adresář v Knihovně (components/contacts) s ukázkovými daty.">
    <SettingsSection title="ContactCard" description="Jeden živý kontakt adresáře: jméno, funkce, organizace a oddělení jako jednořádkový Markdown, e-maily, telefony a web jako odkazy, adresa jako Markdown. S úpravou (editable) se každé pole upravuje na místě: textová pole editorem stránky (jednořádková varianta, Enter nebo klik mimo potvrdí, Esc zruší), identifikátory v obyčejném poli s validací editoru; změna je jen toho pole. Zkratky a přezdívky („PSv“, „Péťa“) jsou slova, podle kterých lidé kontakt hledají: jedna na řádek, každá jednou bez ohledu na velikost písmen a diakritiku, nejvýš 20; výběr osoby po takovém hledání je přidává sám, tady je vidíte, opravíte a smažete. Prázdná pole se ukážou až při úpravě karty (postupné odkrývání): když je klávesnice uvnitř karty (Tab na jméno a dál), nebo po jednom tlačítku Doplnit, místo šesti zástupců na každém řádku. Použijte ji pro kontakt z adresáře v knihovně, na tabuli (karta objektu) a v detailu. Ve stránce ji ukazuje PageContactEditor po kliknutí na propojenou vizitku; blok kontaktů editoru sám zůstává pro kontakt zapsaný ve stránce bez odkazu na adresář.">
      <label class="contacts-example-switch"><Switch v-model="editable" />Úprava na místě</label>
      <div class="contacts-example-cards">
        <div class="contacts-example-card" data-example-contact><ContactCard :contact="card" :editable="editable" :upload="sampleUpload" @change="changed" /></div>
        <div class="contacts-example-card" data-example-compact><ContactCard :contact="card" compact /></div>
      </div>
      <p class="st-muted" data-example-change>Poslední změna: {{ lastChange }}</p>
    </SettingsSection>
    <SettingsSection title="PageContactEditor" description="Úprava kontaktu přímo ve stránce: klik na vizitku nebo zmínku propojenou s adresářem (v editoru, volba editLinked pluginu kontaktů) otevře u ní tento panel. Nahoře je živý kontakt jako ContactCard: pole se upravují na místě a ukládají hned do adresáře, takže se změna ukáže všude, kde je kontakt použit (panel řekne, na kolika dalších místech). Pod tím je to, co patří jen stránce: role, uživatelské jméno a zobrazení v textu; to se do stránky zapíše, když se panel zavře (Esc, klik mimo). Kdo nesmí kontakt měnit, vidí jeho údaje jen ke čtení a stránkové údaje upraví dál. Nepropojený kontakt dál upravuje formulář pluginu. Proč ne formulář pluginu: ten edituje kopii ve stránce, tohle edituje jeden kontakt adresáře, ze kterého stránka čte.">
      <label class="contacts-example-switch"><Switch v-model="pageEditorOpen" />Panel otevřený (vypnutí ho zavře a uloží)</label>
      <div v-if="pageEditorOpen" class="contacts-example-card" data-example-page-contact><PageContactEditor :snapshot="pageSnapshot" :save="pageSave" :store="store" :usage="pageUsage" :forget="() => {}" /></div>
      <p class="st-muted" data-example-page-stored>Zapsáno do stránky: {{ pageStored }}</p>
    </SettingsSection>
    <SettingsSection title="ContactPicker" description="Jedna osoba do textu stránky z klávesnice (/Kontakt a /Kontakt v textu v editoru, kotvené u kurzoru): pište jméno v libovolném pořadí a tvaru (zkuste „jan kovarik“ nebo „pepa“), první je předvybraný, šipky, Enter nebo Tab vloží, první Esc smaže napsané a druhý zavře (prázdné pole nic nevezme a žádný řádek nezvýrazní; první šipka ukáže první řádek a Enter ho pak vezme). Co napíšete hned po výběru z lomítka, je hledání; Enter stisknutý před odpovědí počká a vezme první výsledek odpovědi. Stejná osoba z adresáře, účtů a stránek je jednou (podle e-mailu a id), dva lidé jednoho jména mají řádek s organizací, e-mailem a zdrojem. Poslední řádek Nový kontakt odkryje formulář s napsaným jménem a varuje, když takové jméno už existuje. S vlastností create (řádek „Nový kontakt“ ve výběru osoby po „čekáme na“ v požadavku) se jméno hned porovná s adresářem a rovnou se ptá na shodu, bez hledání a formuláře. Iniciály hledají také: „MM“ najde Marka Musila i Michala Macka, „MMu“ jen Marka a „MMa“ jen Michala (první písmeno jména a začátek příjmení, bez ohledu na diakritiku); v textu stránky se iniciály na kontakt nikdy samy nemění. Zkratky a přezdívky kontaktu adresáře („Mára“) najdou člověka hned jako první řádek; když vyberete osobu po hledání, které není začátkem jejího jména ani e-mailu („MMu“, „Mára“), adresář si ho zapamatuje jako její zkratku (jen kdo smí kontakt měnit) a příště ji najde rovnou; stejnou zkratku mohou mít dva lidé, první je ten, koho pro ni lidé vybírají častěji. Proč ne BoardContactPicker: ten v dialogu zaškrtává několik kontaktů adresáře pro karty tabule; tenhle vkládá jednu osobu u kurzoru. Hledání mají společné (contacts/contactSources.ts).">
      <div class="contacts-example-picker" data-example-contact-picker><ContactPicker :search="searchPeople" :pick="pickPerson" :learn="learnPerson" :close="() => {}" :normalize="normalizeContact" /></div>
      <p class="st-muted" data-example-picked>Vloženo: {{ picked }}</p>
      <p class="st-muted" data-example-learned>Naučená zkratka: {{ learned }}</p>
    </SettingsSection>
    <SettingsSection title="ContactDirectory" description="Knihovna → Kontakty → Adresář: adresář wiki, v Knihovně jako Adresář přehledu kontaktů (slot directory komponenty ContactOverview). Hledá podle libovolného pole, Nový kontakt odkryje formulář pod lištou (InlineReveal) a kontakt s adresou, kterou adresář už má, místo druhého ukáže ten existující (zkuste jana.novakova@example.cz). Řádky přibývají po 40 ms, smazaný řádek se prolne ven a do koše jde nepoužitý kontakt hned, použitý po varování s počtem míst a seznamem „Použito na“ (Zrušit je výchozí, Esc ruší); oznámení „Kontakt … je v koši“ má tlačítko Zpět, které kontakt vrátí na začátek seznamu. Po napsání je první kontakt předvybraný (proužek vlevo), Enter ho upraví tam, kde je (otevře se jeho jméno), a první Esc hledání vymaže; ↓ vstoupí do seznamu. Řádek má také „Co s tím jde dělat“ (P2: Ctrl+mezerník, Alt+Enter, Shift+F10, pravé tlačítko; Upravit a Smazat, sdílený RowActionsMenu). Řádky jsou živé objekty karty (liveObjects), takže změna z tabule se ukáže i tady. Koš (ContactTrash, jen pro toho, kdo smí kontakty přidávat) nahradí adresář košem kontaktů ve sdíleném seznamu koše (TrashList, jako všechny koše): kdo a kdy kontakt odstranil, Obnovit vrátí kontakt beze změny a Smazat trvale ho po potvrzení odstraní i s historií a komentáři. ContactTrash přidá jen záhlaví, Zpět na adresář a načítání po 50; Esc a Zpět vrátí fokus na Koš.">
      <!-- As Knihovna → Kontakty shows it: the directory in ContactOverview's Adresář, Celá wiki one click away. -->
      <ContactOverview :source="entitySource"><template #directory><ContactDirectory :source="source" :store="store" :contacts="duplicates" :usage="{ count: async id => ({ objectId: id, total: 2, visible: 2, hidden: 0 }) }" /></template></ContactOverview>
    </SettingsSection>
    <SettingsSection title="ContactMatchPanel" description="Otázka shody kontaktu přímo ve výběru osoby (ContactPicker v editoru, BoardContactPicker na tabuli), dřív než se osoba vloží: každá vybraná osoba se nejdřív porovná s adresářem (/contacts/match) a vloží se jako odkaz na jeden kontakt (/contacts/resolve). Už v adresáři: řekne, kde se kontakt používá, a pokračuje odkazem. Podobný: Použít existujícího, nebo Vytvořit nového. Rozpor: pro každé lišící se pole vyberete, co platí; Nové zapíše pole do kontaktu, jen když ho smíte měnit. Primární tlačítko má fokus, Esc vrátí do hledání. Proč nová komponenta a ne ConfirmDialog: modální okno přes vyskakovací panel editoru by panel zavřelo a vzalo klávesnici z výběru; tohle je další krok téhož výběru. Stejný kontakt na stránce nebo tabuli podruhé jen ohlásí „Kontakt už tu je – vložen podruhé.“">
      <OptionPills v-model="decisionKind" :options="decisionKinds" label="Shoda" />
      <div class="contacts-example-picker" data-example-contact-match><ContactMatchPanel :key="decisionKind" :decision="decisions[decisionKind]!" @answer="answer" /></div>
      <p class="st-muted" data-example-match-answer>Odpověď: {{ answered }}</p>
    </SettingsSection>
    <SettingsSection title="ContactDuplicates" description="Kontakty → Adresář → Možné duplicity: dvojice kontaktů se stejným telefonem, jménem s organizací nebo podobným jménem (/contacts/duplicates) s důvodem. Sloučit se zeptá, který kontakt zůstane, a ukáže pole, která bude mít (prázdná doplní z druhého, e-maily a telefony obou); druhý jde do koše a jeho použití (stránky, karty na tabulích, uzly stromu) míří na ten, který zůstal; otevřené tabule to uvidí hned bez obnovení. V adresáři výše ji otevře tlačítko Možné duplicity, Esc a Zpět na adresář vrátí fokus na něj. Správci wiki ukáže nahoře i Propojit účty s kontakty (rozhodnutí R3: každý účet má jeden kontakt adresáře; /contacts/accounts): účet a kontakt, které jsou možná jedna osoba, s důvodem (stejné jméno, e-mail chybí nebo se liší). Propojit udělá z kontaktu kontakt účtu a ostatní návrhy účtu zmizí; má-li účet už vlastní kontakt, sloučí se do vybraného a jeho použití ho následují. Není to on založí účtu vlastní kontakt, takže žádný jeho návrh se už nenabídne (rozhodnutí je v datech, nic dalšího se neukládá; návrhy se počítají při čtení), oznámení má Zpět (ten kontakt jde do koše). Obojí i z „Co s tím jde dělat“ řádku (Shift+F10, sdílený RowActionsMenu). Proč ne nová stránka: jde o tutéž otázku „je to jedna osoba?“, jen jednou stranou je účet.">
      <ContactDuplicates :api="duplicates" />
    </SettingsSection>
    <SettingsSection title="ContactLinkPage" description="Nastavení → Kontakty (správce wiki): jednorázové propojení kontaktů zapsaných ve stránkách jen jako text s adresářem. Zobrazit report nic nemění a řekne, kolik se propojí, založí, kolik je nejasných a kolik lidí je jen na neveřejných stránkách. Propojit kontakty je dostupné až s reportem; potvrzení opakuje čísla, pak server běží na pozadí a stránka sleduje postup, ukáže chybu sledování nad ním (běh pokračuje) a po konci dá fokus na výsledek. Při otevření ukáže už běžící nebo skončený běh a odkaz na jeho detail a log v Úlohách na pozadí (běh je běh úlohy contacts.link). Stav přepínáte nahoře; stránka je v ukázce celá, včetně vlastní hlavičky. Není to obecná komponenta, ale jediné místo této úlohy.">
      <OptionPills v-model="linkState" :options="linkStates" label="Stav propojování" />
      <div data-example-contact-link><ContactLinkPage :key="linkState" :api="linkApi" /></div>
    </SettingsSection>
    <SettingsSection title="Živý kontakt a nedostupný kontakt" description="Vizitka s objectId ukazuje živý kontakt adresáře (volba live pluginu kontaktů; v Atlasu /contacts/live po dávkách s krátkou cache), místní role ze stránky („PM projektu“) zůstává. Kontakt v koši nebo smazaný ukáže jméno ze snímku a štítek „nedostupný kontakt“; kontakt, který čtenář nesmí číst, ukáže snímek. Historie a veřejné stránky ukazují snímek své doby.">
      <div data-example-contact-live><!-- @vue-generic {'rich'} --><MarkdownRenderer :value="livePage" profile="rich" ariaLabel="Ukázka živých kontaktů" :plugins="livePlugins" :class="[theme, 'atlas-document-editor']" /></div>
    </SettingsSection>
    <SettingsSection title="ContactLastUsageDialog" description="K2: odebrání karty kontaktu z tabule nebo vizitky přes Odebrat ruší jen odkaz. Když to bylo poslední místo, kde se kontakt používá (služba použití GET /api/usage/{id}/count: počítá i koncepty a místa, která čtenář nevidí; při jediném použití ověří, že je to toto místo), zeptá se: Přesunout do koše (výchozí, koš objektů; trvale smazat jde jen z koše) nebo Ponechat jako nepoužitý (i Esc). Aplikace ho má jednou (App.vue), otázku pokládá contactLinking.ts.">
      <Button variant="outline" type="button" data-example-last-usage @click="removeLast">Odebrat poslední použití</Button>
      <p class="st-muted" data-example-last-usage-answer>Odpověď: {{ lastAnswer }}</p>
      <ContactLastUsageDialog />
    </SettingsSection>
    <SettingsSection title="Přehled stránky: druh Kontakty" description="Obecný přehled stránky (blok overview, plán 3.4 R6) má druhy; každý sečte komponenty svého druhu na stránce, přepíná úroveň jejich zobrazení (volba čtenáře v prohlížeči, data-overview-druh na kořeni editoru) a nabízí akce. Stav nasazení (druh requirements, ukázka v Požadavky ve stránce) použijte na stránce nasazení: procento hotových, nedodané vstupy a čekání, úrovně detailu požadavků. Kontakty (druh contacts, zde) použijte, chce-li čtenář lidi stránky najednou: každý jednou podle objectId (vizitky i zmínky), po organizacích; úrovně Jména / Karty / Plné karty (výchozí, dnešní vizitka) mění vizitky stránky, Stáhnout vše (.vcf) dá jeden soubor vCard a Kopírovat e-maily řádek adresátů. Vkládá se lomítkem „Přehled: …“ (první druh předvybraný, Enter), Kontakty i bez modulu Projector; jednou za druh na stránku. Při čtení stránky (vpravo) je karta stejná; editor bez stránky (vlevo, jako blok na tabuli) přepínač úrovní nemá, protože není kořen stránky, na který by působil. Na tabuli totéž dělá položka menu tabule Stáhnout kontakty (.vcf). Proč ne ContactOverview: ten čte lidi prostoru nebo wiki ze serveru, tento jen lidi této stránky, i v nepublikovaném konceptu.">
      <p class="st-muted" data-example-overview-kinds>Registrované druhy přehledu: {{ kinds }}</p>
      <div class="contacts-example-overview" data-example-contacts-overview>
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="overviewPage" profile="rich" ariaLabel="Ukázková stránka s kontakty" :plugins="overviewPlugins"
          v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
        <!-- @vue-generic {'rich'} --><MarkdownRenderer :value="overviewValue" profile="rich" ariaLabel="Táž stránka při čtení" :plugins="overviewReading" :class="[theme, 'atlas-document-editor']" />
      </div>
    </SettingsSection>
    <SettingsSection title="ContactOverview" description="Kontakty napříč wiki: záložka Kontakty prostoru a Knihovna → Kontakty (katalog entit), jedna komponenta s jedním chováním. Každá osoba jednou, sloučená z vizitek a zmínek ve stránkách a z adresáře podle e-mailu, účtu nebo jména s organizací, s místy, kde se vyskytuje: odkaz otevře stránku u nadpisu, „rozpracováno“ je jen v pracovní verzi, kterou vidí autoři (vysvětluje to i popisek). Přepínač nad hledáním má všude stejná slova: v prostoru Tento prostor / Celá wiki, v Knihovně Adresář / Celá wiki, kde slot directory (ContactDirectory) nahradí seznam. Hledá v libovolném poli a vedle hledání exportuje totéž jako CSV nebo vCard se stejnými právy. Po napsání je první osoba předvybraná (proužek vlevo) a Enter otevře její první výskyt, ↓ na něj jen přejde; první Esc hledání vymaže, prázdné pole Enterem nic neotevře. Proč ne ContactDirectory: adresář je místo, kde se uložené kontakty zakládají a upravují; přehled jen čte a ukazuje i lidi, kteří v adresáři nejsou. Měňte je tam, kde žijí.">
      <ContactOverview space-id="provoz" :source="entitySource" />
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.contacts-example-picker { max-width: 380px; padding: 12px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); }
.contacts-example-overview { display: grid; gap: 16px; grid-template-columns: repeat(auto-fit, minmax(min(100%, 360px), 1fr)); }
.contacts-example-switch { display: inline-flex; align-items: center; gap: 8px; margin-bottom: 12px; font-size: 14px; }
.contacts-example-cards { display: flex; flex-wrap: wrap; gap: 16px; }
.contacts-example-card { flex: 1 1 280px; min-width: 0; max-width: min(420px, 100%); padding: 14px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); box-shadow: var(--refresh-card-shadow); }
</style>
