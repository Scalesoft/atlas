<script setup lang="ts">
import { ref } from "vue";
import { MarkdownRenderer } from "@atlas/markdown/vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { createContactsPlugin, type LiveContact } from "@atlas/markdown/plugins/contacts";
import { readerSkills, rateSkills } from "../../contacts/contactLinking";
import type { ContactSkill, ContactSkills, ExpertList, SkillApi, TopicRef } from "../../contacts/skillApi";
import { useEditorTheme } from "../../editor/editorSetup";
import SkillExperts from "../contacts/SkillExperts.vue";
import SkillRatePanel from "../contacts/SkillRatePanel.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import SearchField from "../settings/SearchField.vue";

// Local sample data only (example.test people): the skills of two people, answered after a short delay as the server would.
const ids = { martin: "a1b2c3d4-0000-4000-8000-000000000071", viktor: "a1b2c3d4-0000-4000-8000-000000000072" };
const skill = (tagId: string, name: string, score: number | null, extra: Partial<ContactSkill> = {}): ContactSkill =>
  ({ tagId, name, score, self: score === null ? null : Math.round(score), peers: 0, fewPeers: false, raters: [], evidence: 0, ...extra });
const people: Record<string, ContactSkills> = {
  [ids.martin]: { contactId: ids.martin, name: "Pavel Horák", self: false, suggested: [], skills: [
    skill("csharp", "C#", 4.6, { peers: 4 }), skill("consent", "Informované souhlasy", 4.2, { peers: 3, selfNote: "vývoj · Orion", evidence: 6 }),
    skill("dotnet", ".NET", 4.5, { peers: 3 }), skill("pg", "PostgreSQL", 4), skill("nh", "NHibernate", 3, { fewPeers: true }), skill("vue", "Vue", 2),
    skill("docker", "Docker", 2), skill("hl7", "HL7", 1)] },
  [ids.viktor]: { contactId: ids.viktor, name: "Tomáš Kolář", self: true, suggested: [{ id: "k8s", name: "Kubernetes", people: 9 }, { id: "gdpr", name: "GDPR", people: 4 }], skills: [
    skill("forms", "Formuláře", 5), skill("consent", "Informované souhlasy", 4, { selfNote: "konfiguračně", mine: 4, myVisibility: "shared" })] },
};
const delay = <T,>(value: T) => new Promise<T>(resolve => setTimeout(() => resolve(structuredClone(value)), 350));
const topics: TopicRef[] = [{ id: "csharp", name: "C#", people: 3, matched: "csharp" }, { id: "consent", name: "Informované souhlasy", people: 7 }, { id: "dotnet", name: ".NET", people: 5 }];
const api: Pick<SkillApi, "contact" | "rate" | "topics" | "experts" | "merge"> = {
  contact: id => delay(people[id]!),
  rate: async (id, request) => {
    const person = people[id]!, found = person.skills.find(item => item.name.toLowerCase() === request.topic.toLowerCase());
    if (request.level === null) person.skills = person.skills.filter(item => item !== found);
    else if (found) Object.assign(found, { mine: request.level, myVisibility: request.visibility, myNote: request.note });
    else person.skills.push(skill(request.topic, request.topic, null, { mine: request.level, myVisibility: request.visibility, myNote: request.note }));
    return delay(person);
  },
  topics: async q => delay({ items: topics.filter(item => `${item.name} ${item.matched ?? ""}`.toLowerCase().includes(q.toLowerCase())) }),
  experts: async q => delay<ExpertList>(q.toLowerCase().includes("soulas") || q.toLowerCase().includes("souhlas") ? {
    topic: { id: "consent", name: "Informované souhlasy", aliases: ["IS", "informovaný souhlas pacienta"], parents: [{ id: "orion", name: "Orion", people: 12 }], children: [] },
    similar: [{ id: "patient", name: "Souhlasy pacientů", people: 2 }],
    people: [
      { contactId: ids.martin, contact: { name: "Pavel Horák", title: "vývojář", organization: "Scalesoft" }, skill: people[ids.martin]!.skills[1], inContext: true, evidence: 6, pages: [] },
      { contactId: ids.viktor, contact: { name: "Tomáš Kolář", title: "konzultant", organization: "Orion" }, skill: people[ids.viktor]!.skills[1], inContext: true, evidence: 2, pages: [] },
    ] } : { topic: null, similar: [], people: [] }),
  merge: async () => delay({ id: "consent", name: "Informované souhlasy", aliases: ["IS", "Souhlasy pacientů"], parents: [], children: [] }),
};

// The page's contacts plugin as Atlas configures it (contactPluginOptions), with the sample answers.
const theme = useEditorTheme();
const live = async (objectId: string): Promise<LiveContact> => ({ state: "ok", contact: objectId === ids.martin
  ? { name: "Pavel Horák", title: "vývojář", organization: "Scalesoft", emails: ["martin.horak@example.test"], objectId }
  : { name: "Tomáš Kolář", title: "konzultant", organization: "Orion", emails: ["viktor.kolar@example.test"], objectId } });
const plugins = [createContactsPlugin({ live, readerSkills: (objectId, { signal }) => readerSkills(objectId, signal, api),
  rateSkills: (objectId, { anchor }) => { rateSkills(objectId, anchor, api); } })];
const mention = (name: string, objectId: string) => ({ type: "contactInline", attrs: { data: { version: 1, contacts: [{ name, objectId }] } } });
const page: MarkdownRichValue = { format: "atlas-rich", version: 1, plugins: [{ id: "contacts", version: 2 }], document: { type: "doc", content: [
  { type: "paragraph", content: [{ type: "text", text: "Informované souhlasy v projektu Orion vyvíjí " }, mention("Pavel Horák", ids.martin),
    { type: "text", text: ", konfiguraci má na starosti " }, mention("Tomáš Kolář", ids.viktor), { type: "text", text: "." }] },
] } } as unknown as MarkdownRichValue;
const query = ref("informované souhlasy");
</script>

<template>
  <SettingsPage title="Znalosti lidí" description="Kdo co umí (docs/skills-design.md): téma je štítek, u osoby (kontaktu adresáře) úroveň 1–5. Žádná nová obrazovka: znalosti jsou v kartě osoby, která se otevře klikem na člověka, a „kdo umí X“ je hledání v adresáři Kontakty.">
    <SettingsSection title="Umí — v kartě osoby" description="Klik na člověka ve stránce otevře jeho kartu jako dosud; hned pod jménem se dočte řádek Umí (témata jako štítky s pěti tečkami úrovně, nejlepší první, nejvýš šest a +N). Karta se ukáže hned, znalosti doběhnou, jakmile odpoví server, a nic pod nimi neposkočí, dokud nepřijdou. Téma vede do adresáře „kdo další to umí“. Ohodnotit otevře panel u téže osoby. Proč ne nový popover: osoba už jednu kartu má; tohle je jedna sekce v ní.">
      <div class="skills-example-page" data-example-skills-page><!-- @vue-generic {'rich'} --><MarkdownRenderer :value="page" profile="rich" ariaLabel="Ukázka stránky s lidmi" :plugins="plugins" :class="[theme, 'atlas-document-editor']" /></div>
    </SettingsSection>
    <SettingsSection title="SkillRatePanel" description="Hodnocení osoby: její témata s vaší úrovní (klik je vyplní k úpravě nebo odebrání), pak jedno pole tématu — existující téma první, Enter ho vezme, nové až když žádné nesedí —, úroveň 1–5, kdo to uvidí a jak. U vlastní karty i témata stránek, které jste psali (jen návrh). Proč ne ContactCard: ta mění údaje kontaktu, tohle vaše hodnocení.">
      <div class="skills-example-panel"><SkillRatePanel :contact-id="ids.viktor" :api="api" /></div>
    </SettingsSection>
    <SettingsSection title="SkillExperts — kdo umí téma v adresáři Kontakty" description="Hledání adresáře je i hledáním tématu: nad kontakty nalezenými podle údajů stojí lidé, kteří téma umí, nejlepší první, s tím, proč. Řádek tématu ukazuje jiné zápisy, širší téma a „Možná totéž“ se Sloučit, takže se různé zápisy uklidí tam, kde je lidé vidí. Proč ne ContactOverview: ten ukazuje lidi ze stránek, tohle řadí lidi podle tématu.">
      <div class="skills-example-directory">
        <SearchField v-model="query" label="Hledat kontakty" placeholder="Jméno, firma, e-mail nebo téma" wide />
        <SkillExperts :query="query" :api="api" />
      </div>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.skills-example-page { max-width: 640px; padding: 12px 16px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); }
.skills-example-panel { max-width: 460px; padding: 16px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); box-shadow: var(--refresh-card-shadow); }
.skills-example-directory { display: grid; gap: 12px; max-width: 760px; }
</style>
