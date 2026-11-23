<script setup lang="ts">
import { ref } from "vue";
import { MarkdownRenderer } from "@atlas/markdown/vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { createContactsPlugin, type LiveContact } from "@atlas/markdown/plugins/contacts";
import { readerSkills, rateSkills } from "../../contacts/contactLinking";
import type { ContactSkill, ContactSkills, ExpertList, SkillApi, SkillGraph as Graph, TopicRef } from "../../contacts/skillApi";
import { useEditorTheme } from "../../editor/editorSetup";
import SkillBars from "../contacts/SkillBars.vue";
import SkillExperts from "../contacts/SkillExperts.vue";
import SkillGraph from "../contacts/SkillGraph.vue";
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
const graph: Graph = { truncated: false, nodes: [
  { id: "consent", kind: "topic", name: "Informované souhlasy", people: 3 }, { id: "csharp", kind: "topic", name: "C#", people: 2 }, { id: "dotnet", kind: "topic", name: ".NET", people: 1 },
  { id: "forms", kind: "topic", name: "Formuláře", people: 1 }, { id: "bpmn", kind: "topic", name: "BPMN", people: 1 },
  { id: ids.martin, kind: "person", name: "Pavel Horák", title: "vývojář", organization: "Scalesoft", people: 0 },
  { id: ids.viktor, kind: "person", name: "Tomáš Kolář", title: "konzultant", organization: "Orion", people: 0 },
  { id: "lucie", kind: "person", name: "Lucie Nováková", title: "analytička", organization: "Scalesoft", people: 0 }],
  links: [{ source: ids.martin, target: "consent", kind: "skill", level: 4.2 }, { source: ids.martin, target: "csharp", kind: "skill", level: 4.6 }, { source: ids.martin, target: "dotnet", kind: "skill", level: 4.5 },
    { source: ids.viktor, target: "consent", kind: "skill", level: 4 }, { source: ids.viktor, target: "forms", kind: "skill", level: 5 }, { source: "lucie", target: "consent", kind: "skill", level: 3 },
    { source: "lucie", target: "bpmn", kind: "skill", level: 5 }, { source: "lucie", target: "csharp", kind: "skill", level: null },
    { source: "csharp", target: "dotnet", kind: "part_of" }, { source: "consent", target: "forms", kind: "related" }] };
const directoryPeople = [{ id: "lucie", name: "Lucie Nováková", organization: "Scalesoft" }, { id: "jan", name: "Jan Dvořák", organization: "Orion" }, { id: "petra", name: "Petra Malá", organization: "Modelsoft" }];
const api: Pick<SkillApi, "contact" | "rate" | "topics" | "experts" | "merge" | "link" | "pages" | "graph"> = {
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
    topic: { id: "consent", name: "Informované souhlasy", aliases: ["IS", "informovaný souhlas pacienta"], parents: [{ id: "orion", name: "Orion", people: 12 }], children: [],
      related: [{ id: "forms", name: "Formuláře", people: 1 }] },
    similar: [{ id: "patient", name: "Souhlasy pacientů", people: 2 }],
    people: [
      { contactId: ids.martin, contact: { name: "Pavel Horák", title: "vývojář", organization: "Scalesoft" }, skill: people[ids.martin]!.skills[1], inContext: true, evidence: 6, pages: [] },
      { contactId: ids.viktor, contact: { name: "Tomáš Kolář", title: "konzultant", organization: "Orion" }, skill: people[ids.viktor]!.skills[1], inContext: true, evidence: 2, pages: [] },
    ] } : { topic: null, similar: [], people: [] }),
  merge: async () => delay({ id: "consent", name: "Informované souhlasy", aliases: ["IS", "Souhlasy pacientů"], parents: [], children: [] }),
  link: async () => delay({ id: "consent", name: "Informované souhlasy", aliases: [], parents: [], children: [] }),
  pages: async () => delay([{ id: "p1", title: "Informované souhlasy – návrh řešení", spaceName: "Orion" }, { id: "p2", title: "Podpis souhlasu na tabletu", spaceName: "Orion" }]),
  graph: async () => delay(graph),
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

// Hundreds of topics (skills spec §7a): one person with 240 topics in 24 groups, a wiki of 320 topics and 90 people, a topic 40 people know.
const groupNames = [".NET", "Java", "Frontend", "Databáze", "Cloud", "DevOps", "Bezpečnost", "Orion", "Zdravotnictví", "Legislativa", "Integrace", "Mobil",
  "Data", "AI", "Testování", "UX", "Sítě", "Linux", "Windows", "Účetnictví", "Projektové řízení", "Analýza", "Podpora", "Školení"];
const level = (i: number) => 1 + ((i * 7) % 5) + ((i % 3) * 0.3 > 0.5 ? 0.5 : 0);
const manySkills: ContactSkill[] = groupNames.flatMap((group, g) => Array.from({ length: 10 }, (_, i) =>
  skill(`${g}-${i}`, i === 0 ? group : `${group} · téma ${i}`, Math.min(5, level(g * 10 + i)), { group: i === 0 ? null : group })));
const manyPeople = Array.from({ length: 90 }, (_, i) => ({ id: `person-${i}`, name: `Kolega ${i + 1}`, organization: ["Scalesoft", "Orion", "Modelsoft"][i % 3] }));
const manyGraph: Graph = (() => {
  const topics = Array.from({ length: 320 }, (_, i) => ({ id: `t${i}`, kind: "topic" as const, name: `${groupNames[i % 24]} ${Math.floor(i / 24) || ""}`.trim(), people: 1 + ((i * 13) % 17) }));
  const kept = [...topics].sort((a, b) => b.people - a.people).slice(0, 60);
  const links = kept.flatMap((topic, i) => Array.from({ length: Math.min(6, topic.people) }, (_, k) => ({ source: `person-${(i * 7 + k * 11) % 90}`, target: topic.id, kind: "skill" as const, level: 1 + ((i + k) % 5) })));
  return { truncated: true, nodes: [...kept, ...manyPeople.map(person => ({ id: person.id, kind: "person" as const, name: person.name, organization: person.organization, people: 0 }))],
    links: [...links, ...kept.slice(1, 20).map((topic, i) => ({ source: topic.id, target: kept[i]!.id, kind: (i % 2 ? "related" : "part_of") as "related" | "part_of" }))] };
})();
const bigApi: Pick<SkillApi, "contact" | "rate" | "topics" | "experts" | "merge" | "link" | "pages" | "graph"> = { ...api,
  contact: async id => delay({ contactId: id, name: "Kolega", self: false, suggested: [], skills: manySkills.slice(0, 40) }),
  experts: async () => delay<ExpertList>({ topic: { id: "java", name: "Java", aliases: ["JVM"], parents: [], children: [{ id: "spring", name: "Spring", people: 12 }], related: [{ id: "kotlin", name: "Kotlin", people: 6 }] },
    similar: [], people: manyPeople.slice(0, 40).map((person, i) => ({ contactId: person.id, contact: { name: person.name, title: ["vývojář", "konzultant", "analytik"][i % 3], organization: person.organization },
      skill: skill("java", "Java", 5 - (i % 5), { peers: i % 4 === 0 ? 3 : 0, fewPeers: i % 4 === 1 }), inContext: false, evidence: (i * 3) % 7, pages: [] })) }) };
const bigQuery = ref("java");
</script>

<template>
  <SettingsPage title="Mapa znalostí" description="Mapa znalostí (docs/skills-design.md): téma je štítek, u osoby (kontaktu adresáře) úroveň 1–5. Žádná nová obrazovka: znalosti jsou v kartě osoby, která se otevře klikem na člověka, a „Kdo umí…“ je hledání v adresáři Kontakty.">
    <SettingsSection title="Umí — v kartě osoby" description="Klik na člověka ve stránce otevře jeho kartu jako dosud; hned pod jménem se dočte řádek Umí (témata jako štítky s pěti tečkami úrovně, nejlepší první, nejvýš šest a +N). Karta se ukáže hned, znalosti doběhnou, jakmile odpoví server, a nic pod nimi neposkočí, dokud nepřijdou. Téma vede do adresáře „kdo další to umí“. Ohodnotit otevře panel u téže osoby. Proč ne nový popover: osoba už jednu kartu má; tohle je jedna sekce v ní.">
      <div class="skills-example-page" data-example-skills-page><!-- @vue-generic {'rich'} --><MarkdownRenderer :value="page" profile="rich" ariaLabel="Ukázka stránky s lidmi" :plugins="plugins" :class="[theme, 'atlas-document-editor']" /></div>
    </SettingsSection>
    <SettingsSection title="SkillRatePanel" description="Hodnocení osoby: její témata s vaší úrovní (klik je vyplní k úpravě nebo odebrání), pak jedno pole tématu — existující téma první, Enter ho vezme, nové až když žádné nesedí —, úroveň 1–5, kdo to uvidí a jak. U vlastní karty i témata stránek, které jste psali (jen návrh). Proč ne ContactCard: ta mění údaje kontaktu, tohle vaše hodnocení.">
      <div class="skills-example-panel"><SkillRatePanel :contact-id="ids.viktor" :api="api" /></div>
    </SettingsSection>
    <SettingsSection title="SkillExperts — kdo umí téma v adresáři Kontakty" description="Hledání adresáře je i hledáním tématu: nad kontakty nalezenými podle údajů stojí lidé, kteří téma umí, nejlepší první, s tím, proč. Řádek tématu ukazuje jiné zápisy, širší téma a „Možná totéž“ se Sloučit, takže se různé zápisy uklidí tam, kde je lidé vidí. Proč ne ContactOverview: ten ukazuje lidi ze stránek, tohle řadí lidi podle tématu.">
      <div class="skills-example-directory">
        <SearchField v-model="query" label="Hledat kontakty" placeholder="Jméno, firma, e-mail nebo téma" wide />
        <SkillExperts :query="query" :api="api" :people="directoryPeople" />
      </div>
    </SettingsSection>
    <SettingsSection title="SkillGraph — Mapa znalostí (třetí pohled v Kontaktech)" description="Graf kdo–co vedle Adresáře a Celé wiki: lidé a témata, síla čáry je úroveň, kterou čtenář smí vidět; témata spojuje „je součástí“ (plná) a „souvisí“ (čárkovaná). Filtry organizace, prostor a téma. Klik na téma otevře jeho přehled v adresáři, na osobu její kartu pod grafem s pruhy znalostí. Seznam je týž graf pro klávesnici; pod 680 px místa je jen seznam. Proč ne graf vazeb stránek (Obsah prostoru): ten ukazuje stránky a objekty prostoru, tohle lidi a témata napříč wiki.">
      <SkillGraph :api="api" :data="graph" :spaces="[{ id: 's1', name: 'Orion' }, { id: 's2', name: 'Začínáme' }]" />
    </SettingsSection>
    <SettingsSection title="SkillBars — znalosti osoby" description="Přehled osoby: pruh na téma, délka je úroveň, seskupeno podle širšího tématu (shluky), skupiny podle nejlepšího tématu. Pruhy místo radaru: čtou se při jakémkoli počtu témat i na 375 px. Ukazuje je detail kontaktu v doku a osoba vybraná v grafu (ContactSkillLine s bars).">
      <div class="skills-example-panel"><SkillBars name="Pavel Horák" :skills="people[ids.martin]!.skills.map(skill => ({ ...skill, group: ['C#', 'NHibernate'].includes(skill.name) ? '.NET' : skill.name === 'Informované souhlasy' ? 'Orion' : null }))" /></div>
    </SettingsSection>
    <SettingsSection title="Stovky témat — jak to vypadá, když je toho hodně" description="Zátěžová ukázka (generovaná data): osoba s 240 tématy ve 24 skupinách — v každé skupině 5 nejlepších a Zobrazit další, prvních 6 skupin a zbytek za jedním tlačítkem, od 20 témat filtr. Téma, které umí 40 lidí — 12 karet a Zobrazit další. Wiki s 320 tématy a 90 lidmi — graf drží 60 témat, která zná nejvíc lidí (poznámka pod grafem), zbytek najde filtr nebo seznam; v seznamu u tématu 8 lidí a +N. Skupiny, karty a řádky mimo obraz se nevykreslují (content-visibility).">
      <div class="skills-example-panel skills-example-wide"><SkillBars name="Kolega s 240 tématy" :skills="manySkills" /></div>
      <div class="skills-example-directory"><SkillExperts :query="bigQuery" :api="bigApi" :people="manyPeople" /></div>
      <SkillGraph :api="bigApi" :data="manyGraph" />
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.skills-example-page { max-width: 640px; padding: 12px 16px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); }
.skills-example-panel { max-width: 460px; padding: 16px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); box-shadow: var(--refresh-card-shadow); }
.skills-example-directory { display: grid; gap: 12px; }
.skills-example-wide { max-width: none; margin-bottom: 16px; }
</style>
