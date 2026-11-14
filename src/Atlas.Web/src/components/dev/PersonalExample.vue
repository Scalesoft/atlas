<script setup lang="ts">
import { ref } from "vue";
import type { accessApi } from "../../accessApi";
import { ApiError } from "../../api";
import type { ObjectDto, TrashedObject } from "../../objects/objectApi";
import { noteTypeId, type NoteData, type PersonalApi, type PersonalItem } from "../../personal/personalApi";
import NoteCard from "../personal/NoteCard.vue";
import PersonalItems from "../personal/PersonalItems.vue";
import ShareItemDialog from "../personal/ShareItemDialog.vue";
import ResourceShareForm from "../settings/access/ResourceShareForm.vue";
import type { AccessCandidate, AccessGrantBody, ResourceAccess } from "../settings/access/accessModel";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import { Button } from "../ui/button";

// Local sample data only: an in-memory personal space and access list, nothing leaves the gallery.
const now = "2026-10-29T09:00:00Z";
let next = 10;
const texts: Record<string, NoteData> = {
  n1: { title: "**Porada** s týmem", text: "- rozpočet\n- _termíny_ nasazení" },
  n2: { title: "Nákup", text: "" },
  s1: { title: "Runbook zálohy", text: "1. Zkontrolovat **Git** zálohu\n2. Obnovit do prázdné instalace" },
  s2: { title: "Plán konference", text: "Program, sály a **řečníci**." },
};
const item = (id: string, title: string, extra: Partial<PersonalItem> = {}): PersonalItem =>
  ({ id, typeId: noteTypeId, title, updatedAt: now, version: 1, canEdit: true, shares: 0, ownerName: null, level: null, expiresAt: null, excerpt: "", ...extra });
const mine = ref<PersonalItem[]>([
  item("n1", "Porada s týmem", { shares: 2, excerpt: "rozpočet termíny nasazení" }),
  item("n2", "Nákup"),
]);
const shared = ref<PersonalItem[]>([
  item("s1", "Runbook zálohy", { ownerName: "Jana Nováková", level: "write", expiresAt: "2026-11-30T23:00:00Z", excerpt: "Zkontrolovat Git zálohu Obnovit do prázdné instalace" }),
  item("s2", "Plán konference", { canEdit: false, ownerName: "Petr Svoboda", level: "read", excerpt: "Program, sály a řečníci." }),
]);
type Trashed = PersonalItem & { trashedAt: string };
const trashed = ref<Trashed[]>([{ ...item("t1", "Stará poznámka", { shares: 1 }), trashedAt: now }]);
// As GET /api/objects/trash?type=atlas.note&scope=private answers it.
const trashRow = (entry: Trashed): TrashedObject => ({ id: entry.id, typeId: noteTypeId, scope: "private", spaceId: null, title: entry.title, version: entry.version,
  data: null, trashedAt: entry.trashedAt, trashedById: "me", trashedByName: "Vy" });
const source: PersonalApi = {
  mine: async () => mine.value,
  shared: async () => shared.value,
  async note(id) {
    // A moment of loading, so the gallery shows the sized skeleton of an opening note.
    await new Promise(resolve => setTimeout(resolve, 400));
    const found = [...mine.value, ...shared.value].find(entry => entry.id === id)!;
    return { version: found.version, canEdit: found.canEdit, data: texts[id] ?? { title: found.title, text: "" } };
  },
  async createNote(title) {
    const id = `n${next++}`;
    texts[id] = { title, text: "" };
    mine.value = [item(id, title), ...mine.value];
    return { id, typeId: noteTypeId, title, version: 1, updatedAt: now, scope: "private", spaceId: null, ownerId: "me", createdAt: now, createdBy: "me", editingModel: "fields",
      canEdit: true, data: { title, text: "" } } satisfies ObjectDto;
  },
  async changeNote(id, fields: Partial<NoteData>) {
    texts[id] = { ...texts[id]!, ...fields };
    const found = [...mine.value, ...shared.value].find(entry => entry.id === id)!;
    return { version: ++found.version, duplicate: false };
  },
  async trash(target) {
    const found = mine.value.find(entry => entry.id === target.id);
    mine.value = mine.value.filter(entry => entry.id !== target.id);
    if (found) trashed.value = [{ ...found, trashedAt: new Date().toISOString() }, ...trashed.value];
  },
  trashList: async () => ({ items: trashed.value.map(trashRow), total: trashed.value.length, page: 1, pageSize: 50 }),
  async restore(id) {
    const found = trashed.value.find(entry => entry.id === id);
    trashed.value = trashed.value.filter(entry => entry.id !== id);
    if (found) { const { trashedAt: _, ...back } = found; mine.value = [back, ...mine.value]; }
    return { version: 2, duplicate: false };
  },
  async purge(id) { trashed.value = trashed.value.filter(entry => entry.id !== id); },
};

// Who reaches the note as GET /api/access/resources/item/{id} answers it: the owner's locked row, a group with an end, a person.
const access = ref<ResourceAccess>({
  resource: { kind: "item", id: "n1", title: "Porada s týmem", parentTitle: "Poznámka" },
  canManage: true,
  automatic: [{ label: "Vlastník", detail: "Vy: jediný sdílí, mění přístupy a maže", level: "manage", count: 1 }],
  entries: [
    { subject: { kind: "group", id: "g1", name: "Provoz", memberCount: 4, detail: "4 členové" }, level: "read",
      sources: [{ via: "direct", label: "Přímo přidělený přístup", level: "read", version: "v1", expiresAt: "2026-12-31T23:00:00Z", effective: true }] },
    { subject: { kind: "user", id: "u2", name: "Eva Malá", detail: "eva.mala", isActive: true }, level: "write",
      sources: [{ via: "direct", label: "Přímo přidělený přístup", level: "write", version: "v1", effective: true }] },
  ],
  hasMore: false,
});
// The search as the server answers it: the owner ("Vy") is left out with exceptMe; Eva and Provoz already have access.
const people: AccessCandidate[] = [
  { kind: "user", id: "me", name: "Vy (vlastník)", detail: "ja" },
  { kind: "user", id: "u3", name: "Karel Novák", detail: "karel" },
  { kind: "user", id: "u2", name: "Eva Malá", detail: "eva.mala" },
  { kind: "group", id: "g1", name: "Provoz", detail: "4" },
  { kind: "group", id: "g2", name: "Vedení", detail: "3" },
];
const shareApi: Pick<typeof accessApi, "resource" | "candidates" | "grant" | "revoke"> = {
  resource: async () => access.value,
  async candidates(kind, q, exceptMe) {
    await new Promise(resolve => setTimeout(resolve, 300));
    return people.filter(person => person.kind === kind && !(exceptMe && person.id === "me") && person.name.toLowerCase().includes(q.toLowerCase()));
  },
  async grant(_kind, _id, body: AccessGrantBody) {
    const subject = people.find(person => person.id === body.subjectId) ?? access.value.entries.find(entry => entry.subject.id === body.subjectId)?.subject;
    if (!subject) throw new ApiError(404, "Uživatel nebyl nalezen.");
    const rest = access.value.entries.filter(entry => entry.subject.id !== body.subjectId);
    access.value = { ...access.value, entries: [...rest, { subject: { kind: subject.kind, id: subject.id, name: subject.name }, level: body.level,
      sources: [{ via: "direct", label: "Přímo přidělený přístup", level: body.level, version: `v${next++}`, expiresAt: body.expiresAt ?? null, effective: true }] }] };
  },
  async revoke(_kind, _id, _subjectKind, subjectId) { access.value = { ...access.value, entries: access.value.entries.filter(entry => entry.subject.id !== subjectId) }; },
};
const shareOpen = ref(false);
const lastShare = ref("zatím nic");
const card = ref<NoteData>({ title: "**Návrh** osobního prostoru", text: "Poznámka je _soukromá_, dokud ji nesdílím.\n\n- název je jednořádkový Markdown\n- text je plný Markdown" });
const lastChange = ref("zatím nic");
function changed(fields: Partial<NoteData>) {
  card.value = { ...card.value, ...fields };
  lastChange.value = JSON.stringify(fields);
}
</script>

<template>
  <SettingsPage title="Osobní prostor a sdílení" description="Můj prostor, Sdíleno se mnou a sdílení jedné poznámky (components/personal, spec 2026-10-29) s ukázkovými daty. Osobní tabule vlastník 29. 10. vyřadil.">
    <SettingsSection title="PersonalItems: Můj prostor" description="Karta Knihovny „Můj prostor“ (mode mine): soukromé poznámky, nejnovější změna první. Každý řádek stejně: ikona, název, úryvek textu a počet sdílení; seznam posílá jen úryvky. Klik nebo Enter poznámku rozbalí na místě (InlineReveal) a teprve pak načte celý text, mezitím kostra velikosti poznámky; NoteCard ji upravuje editorem stránky. Nová poznámka odkryje pod lištou pole pro název, Enter ji založí a otevře rovnou v psaní textu. Sdílet otevře ShareItemDialog; Sdílet a Smazat má řádek také v nabídce „Co s tím jde dělat“ (Ctrl+mezerník, Shift+F10, pravé tlačítko; RowActionsMenu). Tlačítko koše u řádku poznámku přesune do koše hned (je vratné: oznámení s „Zpět“ ji vrátí, klávesnice přejde na další řádek) a tlačítko Koš (PersonalTrash nad sdíleným seznamem koše TrashList, jednou cestou objektů) poznámky obnoví i se sdílením, nebo smaže trvale. Hledání filtruje během psaní a schová se, dokud není co hledat; po napsání je první poznámka předvybraná (proužek vlevo) a Enter ji otevře, v prázdném hledání Enter nic neotevře, ↓ vezme první řádek a první Esc hledání vymaže. Proč ne ContactDirectory: adresář je jeden pro celou wiki; tohle jsou poznámky jednoho člověka.">
      <PersonalItems mode="mine" :source="source" :share-api="shareApi" />
    </SettingsSection>
    <SettingsSection title="PersonalItems: Sdíleno se mnou" description="Karta „Sdíleno se mnou“ (mode shared): cizí poznámky, které mi někdo sdílí, s vlastníkem, úrovní a koncem platnosti. Úroveň Upravuje dovolí upravit poznámku na místě; sdílet dál ani mazat nejde.">
      <PersonalItems mode="shared" :source="source" />
    </SettingsSection>
    <SettingsSection title="NoteCard" description="Jedna soukromá poznámka: název jako jednořádkový Markdown, text jako Markdown, obojí s úpravou na místě editorem stránky (Enter v názvu, Esc nebo klik mimo potvrdí). Změna je jen toho pole. Proč ne ContactCard: kontakt má pole osoby, poznámka název a volný text; proč ne stránka: poznámka nemá strom, historii ani publikaci.">
      <NoteCard :note="card" editable @change="changed" />
      <p class="st-muted" data-example-change>Poslední změna: {{ lastChange }}</p>
    </SettingsSection>
    <SettingsSection title="ResourceShareForm" description="settings/access/ResourceShareForm.vue — sdílení jedné položky v jednom kompaktním formuláři (postupně odkrývaný formulář místo průvodce): Komu (lidé i skupiny v jednom hledání, vlastník vynechaný, první výsledek předvybraný), Smí (Čte / Upravuje, výchozí Čte) a jen na požádání Omezit platnost. Enter v hledání vybere a předá fokus úrovni, další Enter (nebo Sdílet) sdílí; živá věta říká, co se stane. Kdo už přístup má, je v nabídce vidět, ale vybrat nejde. Kdy co: ResourceShareForm pro jednu položku se dvěma prostými úrovněmi bez nového přihlášení (osobní poznámka); ResourceGrantFlow (GrantFlow, pět kroků) pro prostory, stránky a trezory se schodištěm úrovní, začátkem platnosti a ověřením přihlášením.">
      <ResourceShareForm kind="item" id="n1" title="Porada s týmem" :taken="access.entries.map(entry => `${entry.subject.kind}:${entry.subject.id}`)" :api="shareApi" @shared="lastShare = $event" />
      <p class="st-muted" data-example-share-result>Poslední sdílení: {{ lastShare }}</p>
    </SettingsSection>
    <SettingsSection title="ShareItemDialog" description="Sdílet jednu poznámku: ResourceShareForm nahoře a pod ním dosavadní sdílení (ResourceAccess druhu item v kompaktní podobě: zamčený řádek vlastníka, lidé a skupiny s úrovní a platností, Změnit a Odebrat na místě). Dialog otevře s fokusem v hledání. Proč ne stránka přístupů: osobní poznámka nemá vlastní nastavení, sdílí se z místa, kde je.">
      <Button variant="outline" data-example-share @click="shareOpen = true">Sdílet poznámku</Button>
      <ShareItemDialog :open="shareOpen" item-id="n1" title="Porada s týmem" :api="shareApi" @close="shareOpen = false" />
    </SettingsSection>
  </SettingsPage>
</template>
