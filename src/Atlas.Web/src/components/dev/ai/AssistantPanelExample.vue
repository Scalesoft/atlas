<script setup lang="ts">
import { computed, onBeforeUnmount, provide, ref, watch } from "vue";
import { ApiError } from "../../../api";
import { PROPOSAL_DECIDE, PROPOSAL_FINDINGS, type Proposal, type ProposalItem } from "../../../assistant/proposals";
import { linkFindings } from "../../../publishing/publicationChecks";
import { aiUnavailableText } from "../../../ai/aiAction";
import { useAssistant, type AssistantContext, type ThreadItem } from "../../../assistant/useAssistant";
import AssistantPanel from "../../assistant/AssistantPanel.vue";
import AssistantPanelHeader from "../../assistant/AssistantPanelHeader.vue";
import AtlasIcon from "../../AtlasIcon.vue";
import OptionPills from "../../settings/OptionPills.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { provenance, sentences, sources } from "./aiSampleData";

// AssistantPanel (plan phase 3 W10; refresh 2.5 board „Asistent – vstupy a průběh“). The real panel with local data: the gallery seeds the shared assistant state and never
// calls the server (without the capability the composer cannot send). Switch the scene; the frame is the non-modal
// WorkspaceSidePanel column (400 px, 360 px narrow); below 760 px the real panel covers the screen.
const assistant = useAssistant();
const context: AssistantContext = { pageId: "page-1", pageTitle: "Připojení k VPN", spaceId: "space-1", spaceName: "IT a infrastruktura", canWrite: true };
const scene = ref("conversation");
const narrow = ref(false);
// Sample of GET /api/assistant/tools for „Co asistent s nástroji umí“ (the info button beside the switch); the gallery never calls the server.
const toolGroups = [
  { group: "zaklad", tools: [{ name: "atlas_search", description: "Hledá ve wiki.", write: false }, { name: "atlas_get_page", description: "Přečte stránku.", write: false }] },
  { group: "stitky", tools: [{ name: "atlas_apply_labels", description: "Navrhne tagy a ikony.", write: true }] },
  { group: "koncepty", tools: [{ name: "atlas_update_section", description: "Navrhne úpravu sekce.", write: true }] },
];
const scenes = [
  { value: "conversation", label: "Konverzace", icon: "quote" },
  { value: "working", label: "Průběh odpovědi", icon: "history" },
  { value: "steps", label: "Kroky běží", icon: "search" },
  { value: "proposal", label: "Návrh štítků", icon: "tag" },
  { value: "proposal-done", label: "Provedeno", icon: "check" },
  { value: "proposal-conflict", label: "Konflikt 409", icon: "triangle-alert" },
  { value: "proposal-40", label: "40 položek", icon: "list" },
  { value: "proposal-section", label: "Návrh úpravy sekce", icon: "pencil" },
  { value: "proposal-reformat", label: "Přeformátování", icon: "page" },
  { value: "proposal-removed", label: "Komponenta by se ztratila (409)", icon: "triangle-alert" },
  { value: "proposal-move", label: "Návrh přesunu", icon: "folder-input" },
  { value: "proposal-menu", label: "Uspořádání menu", icon: "menu" },
  { value: "proposal-publish", label: "Návrh publikace", icon: "published" },
  { value: "tools-on", label: "S nástroji", icon: "search" },
  { value: "no-right", label: "Bez práva", icon: "lock" },
  { value: "selection", label: "Výběr ze stránky", icon: "page" },
  { value: "scope", label: "Změna rozsahu", icon: "folder" },
  { value: "list", label: "Moje konverzace", icon: "list" },
  { value: "outdated", label: "Zastaralá", icon: "history" },
  { value: "unavailable", label: "Nedostupná", icon: "lock" },
  { value: "outage", label: "Výpadek", icon: "triangle-alert" },
  { value: "empty", label: "Nová konverzace", icon: "plus" },
];

let key = 0;
const question = (text: string): ThreadItem => ({ kind: "question", key: `q${++key}`, text });
const divider = (text: string): ThreadItem => ({ kind: "scope", key: `d${++key}`, text });
const answer = (text: string, over: Partial<Extract<ThreadItem, { kind: "answer" }>> = {}): ThreadItem => ({
  kind: "answer", key: `a${++key}`, messageId: `m${key}`, question: text, state: "done", sentences, sources, provenance, createdAt: new Date().toISOString(), rating: null, scope: { kind: "page", pageId: "page-1", spaceId: "space-1" }, ...over,
});

// A proposal of the agent (WP-20) with sample pages; the gallery answers the decision itself, the server is never called.
const PAGES = ["Instalace VPN", "Připojení k Wi-Fi", "Reset hesla", "Nová pošta", "Tiskárny", "Zálohování", "Onboarding IT", "Správa certifikátů", "Mobilní zařízení", "Licence"];
const ICONS = ["lock", "compass", "star", "book-open", "home", "lightbulb"];
function sampleProposal(count: number, over: Partial<Proposal> = {}): Proposal {
  const items: ProposalItem[] = Array.from({ length: count }, (_, index) => {
    const title = PAGES[index % PAGES.length]! + (index >= PAGES.length ? ` ${Math.floor(index / PAGES.length) + 1}` : "");
    return {
      index, pageId: `page-${index}`, title, path: `IT › ${index % 3 === 0 ? "Síť" : index % 3 === 1 ? "Účty" : "Zařízení"} › ${title}`, kind: "labels", status: null,
      before: { icon: index % 4 === 0 ? null : "page", color: index % 4 === 0 ? null : "slate", tags: index % 2 ? ["provoz", "starý"] : [] },
      after: { icon: ICONS[index % ICONS.length]!, color: index % 2 ? "blue" : "teal", tags: index % 2 ? ["provoz", "IT"] : ["IT", "návod"] },
    };
  });
  return { id: "proposal-1", messageId: "m1", tool: "atlas_apply_labels", state: "pending", expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(), items, ...over };
}
function appliedProposal(): Proposal {
  const proposal = sampleProposal(8, { state: "applied" });
  proposal.items.forEach((item) => { item.status = item.index === 5 ? "failed" : item.index === 7 ? "skipped" : "applied"; item.code = item.index === 5 ? "tags_changed" : null; });
  return proposal;
}
// Draft proposals (WP-31): one item each, the changed sections as the server sends them (placeholders as „⟦komponenta: …⟧“).
const CONTACT = "⟦komponenta: Kontakt Jana Nováková⟧";
const DRAFTS: Record<string, { question: string; answer: string; tool: string; sections: string[]; before: string; after: string }> = {
  "proposal-section": {
    question: "Uprav sekci Kontakty: doplň noční pohotovost", answer: "Navrhuji doplnit do sekce Kontakty noční pohotovost. Kontakt zůstává.", tool: "atlas_update_section",
    sections: ["Kontakty"], before: `## Kontakty\n\nPři potížích volejte ${CONTACT} v pracovní době.`,
    after: `## Kontakty\n\nPři potížích volejte ${CONTACT} v pracovní době, v noci pohotovost IT na lince 2222.`,
  },
  "proposal-reformat": {
    question: "Přeformátuj tuto stránku podle stránky Instalace VPN", answer: "Navrhuji stránku uspořádat jako Instalace VPN: Postup, Řešení potíží a Kontakty. Text i kontakt zůstávají.", tool: "atlas_replace_page",
    sections: ["Postup", "Řešení potíží", "Kontakty", "Připojení"],
    before: `## Připojení\n\nNainstalujte WireGuard a naskenujte QR kód. Při chybě 7200 obnovte certifikát. Při potížích volejte ${CONTACT}.`,
    after: `## Postup\n\nNainstalujte WireGuard a naskenujte QR kód.\n\n## Řešení potíží\n\nPři chybě 7200 obnovte certifikát.\n\n## Kontakty\n\nPři potížích volejte ${CONTACT}.`,
  },
  "proposal-removed": {
    question: "Přeformátuj tuto stránku podle stránky Instalace VPN", answer: "Navrhuji stránku uspořádat jako Instalace VPN.", tool: "atlas_replace_page",
    sections: ["Postup", "Připojení"], before: `## Připojení\n\nNainstalujte WireGuard. Při potížích volejte ${CONTACT}.`, after: "## Postup\n\nNainstalujte WireGuard.",
  },
};
function draftProposal(name: string, over: Partial<Proposal> = {}): Proposal | null {
  const draft = DRAFTS[name];
  if (!draft) return null;
  const failed = name === "proposal-removed";
  return {
    id: "proposal-1", messageId: "m1", tool: draft.tool, state: failed ? "failed" : "pending", expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
    items: [{ index: 0, pageId: "page-1", title: "Připojení k VPN", path: "IT a infrastruktura › Připojení k VPN", kind: "content", before: null, after: null,
      status: failed ? "failed" : null, code: failed ? "components_removed" : null, text: { sections: draft.sections, before: draft.before, after: draft.after } }],
    ...over,
  };
}
// A move (WP-40) and a publication (WP-41): one page each; the facts the server sends, the publication with one unpublished link.
const SCOPE: Record<string, { question: string; answer: string; tool: string; item: Partial<ProposalItem>; more?: Partial<ProposalItem>[] }> = {
  "proposal-move": {
    question: "Přesuň stránku Tiskárny pod stránku Kancelář", answer: "Navrhuji přesunout stránku Tiskárny i s podstránkami pod stránku Kancelář.", tool: "atlas_move_page",
    item: { pageId: "page-2", title: "Tiskárny", path: "IT a infrastruktura › Tiskárny", kind: "move",
      move: { before: "IT a infrastruktura › Tiskárny", after: "IT a infrastruktura › Kancelář › Tiskárny", subpages: 2, otherSpace: false } },
  },
  // The left menu (atlas_arrange_pages): one row per move, played in order; the last one only changes the position among siblings.
  "proposal-menu": {
    question: "Poskládej levé menu prostoru IT a infrastruktura", answer: "Navrhuji dát tiskárny a Wi-Fi pod Kancelář a VPN na začátek. Vyberte přesuny a potvrďte je najednou.", tool: "atlas_arrange_pages",
    item: { pageId: "page-2", title: "Tiskárny", path: "IT a infrastruktura › Tiskárny", kind: "move",
      move: { before: "IT a infrastruktura › Tiskárny", after: "IT a infrastruktura › Kancelář › Tiskárny", subpages: 2, otherSpace: false } },
    more: [
      { pageId: "page-4", title: "Wi-Fi pro hosty", path: "IT a infrastruktura › Wi-Fi pro hosty", kind: "move",
        move: { before: "IT a infrastruktura › Wi-Fi pro hosty", after: "IT a infrastruktura › Kancelář › Wi-Fi pro hosty (před Tiskárny)", subpages: 0, otherSpace: false } },
      { pageId: "page-1", title: "Připojení k VPN", path: "IT a infrastruktura › Návody › Připojení k VPN", kind: "move",
        move: { before: "IT a infrastruktura › Návody › Připojení k VPN", after: "IT a infrastruktura › Připojení k VPN (před Kancelář)", subpages: 0, otherSpace: false } },
    ],
  },
  "proposal-publish": {
    question: "Publikuj tuto stránku", answer: "Navrhuji publikovat verzi 7 stránky Připojení k VPN. Kontrola našla odkaz na nepublikovanou stránku.", tool: "atlas_publish",
    item: { pageId: "page-1", title: "Připojení k VPN", path: "IT a infrastruktura › Připojení k VPN", kind: "publish",
      publish: { version: 7, publishedVersion: 5, changedSections: 2, changeSummary: "Doplněn postup pro macOS",
        findings: { version: 7, links: [{ targetPageId: "page-3", state: "unpublished", title: "Certifikáty zařízení" }] } } },
  },
};
function scopeProposal(name: string, over: Partial<Proposal> = {}): Proposal | null {
  const entry = SCOPE[name];
  if (!entry) return null;
  return {
    id: "proposal-1", messageId: "m1", tool: entry.tool, state: "pending", expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
    items: [entry.item, ...(entry.more ?? [])].map((item, index) => ({ pageId: "", title: "", path: "", kind: "labels", before: null, after: null, status: null, code: null, ...item, index })),
    ...over,
  };
}
const editorOpened = ref("");
// The check before publication without the server: the link findings it sent (the browser's rules need the saved page).
provide(PROPOSAL_FINDINGS, async (request) => linkFindings(request.findings.links).map(({ target: _target, ...check }) => check));
provide(PROPOSAL_DECIDE, async (id, approve, indexes) => {
  await new Promise((resolve) => setTimeout(resolve, 500));
  if (scene.value === "proposal-conflict") throw new ApiError(409, "Návrh vypršel.", "proposal_expired");
  const proposal = draftProposal(scene.value, { id, state: approve ? "applied" : "declined" })
    ?? scopeProposal(scene.value, { id, state: approve ? "applied" : "declined" })
    ?? sampleProposal(scene.value === "proposal-40" ? 40 : 8, { id, state: approve ? "applied" : "declined" });
  proposal.items.forEach((item) => { item.status = approve ? (indexes.includes(item.index) ? "applied" : "skipped") : null; });
  return proposal;
});

function seed(name: string) {
  assistant.reset();
  assistant.setContext(context);
  assistant.open.value = true;
  const items: ThreadItem[] = [];
  if (name === "conversation" || name === "tools-on" || name === "no-right" || name === "scope" || name === "outdated" || name === "unavailable" || name === "outage") {
    items.push(question("Jak se připojím k firemní VPN?"), answer("Jak se připojím k firemní VPN?"));
    items.push(question("A co když certifikát vypršel?"), answer("A co když certifikát vypršel?", { sentences: [{ text: "Požádejte o nový certifikát přes správce zařízení.", sources: [2] }], sources: sources.slice(1, 3) }));
  }
  if (name === "scope") {
    items.push(divider("Rozsah změněn na: Celá dostupná wiki"), question("Kde najdu pravidla pro práci z domova?"), answer("Kde najdu pravidla pro práci z domova?", { sentences: [{ text: "Pravidla jsou na stránce Práce z domova.", sources: [3] }], sources: sources.slice(2), scope: { kind: "wiki" } }));
  }
  if (name === "working") {
    items.push(question("Proč používáme Composition API a ne Options API?"), answer("Proč používáme Composition API a ne Options API?", { state: "preparing", sentences: [], provenance: null, createdAt: null }));
  }
  if (name === "steps") {
    items.push(question("Které stránky v prostoru IT nemají žádný tag?"), answer("Které stránky v prostoru IT nemají žádný tag?", {
      state: "preparing", sentences: [], provenance: null, createdAt: null, scope: { kind: "space", spaceId: "space-1" },
      steps: [{ id: "s1", label: "Procházím strom prostoru IT a infrastruktura", state: "done" }, { id: "s2", label: "Čtu stránku Instalace VPN", state: "done" }, { id: "s3", label: "Hledám zmínky", state: "now" }],
    }));
  }
  if (name === "conversation") {
    const last = items.at(-1);
    if (last?.kind === "answer") last.steps = [{ id: "s1", label: "Hledám VPN", state: "done" }, { id: "s2", label: "Čtu stránku Připojení k VPN", state: "done" }];
  }
  editorOpened.value = "";
  // „Otevřít v editoru“ goes up to App.vue (openPage(id, true)); the gallery only says what would open.
  assistant.setEditor((pageId) => { editorOpened.value = `Otevřelo by stránku ${pageId} v editoru.`; });
  const draft = DRAFTS[name];
  const scoped = SCOPE[name];
  if (scoped) {
    items.push(question(scoped.question), answer(scoped.question, { sentences: [{ text: scoped.answer, sources: [] }], sources: [], steps: [{ id: "s1", label: "Procházím strom prostoru IT a infrastruktura", state: "done" }], proposal: scopeProposal(name)! }));
  } else if (draft) {
    items.push(question(draft.question), answer(draft.question, { sentences: [{ text: draft.answer, sources: [] }], sources: [], steps: [{ id: "s1", label: "Čtu stránku Instalace VPN", state: "done" }, { id: "s2", label: "Čtu stránku Připojení k VPN", state: "done" }], proposal: draftProposal(name)! }));
  } else if (name.startsWith("proposal")) {
    items.push(question("Navrhni ikonky pro stránky v IT"));
    const proposal = name === "proposal-done" ? appliedProposal() : sampleProposal(name === "proposal-40" ? 40 : 8);
    items.push(answer("Navrhni ikonky pro stránky v IT", { sentences: [{ text: `Navrhuji změny u ${proposal.items.length} stránek v prostoru IT. Vyberte, které chcete provést.`, sources: [] }], sources: [], steps: [{ id: "s1", label: "Procházím strom prostoru IT a infrastruktura", state: "done" }], proposal }));
  }
  // Tools are off by default (owner, 2026-10-28); the agent scenes show them turned on.
  if (name === "tools-on" || name === "steps" || draft || scoped || name.startsWith("proposal")) assistant.tools.value = true;
  if (name === "outdated") assistant.readOnly.value = "outdated";
  if (name === "unavailable") {
    items[3] = answer("A co když certifikát vypršel?", { state: "withheld", sentences: [], sources: [] });
    assistant.readOnly.value = "unavailable";
  }
  if (name === "outage") {
    items.push(question("Co dělat při ztrátě notebooku?"), answer("Co dělat při ztrátě notebooku?", { state: "unavailable", sentences: [], sources: [], text: aiUnavailableText() }));
    assistant.outage.value = true;
  }
  assistant.items.value = items;
  if (name === "selection") assistant.attachSelection("Composition API implementované v souborech ve formátu SFC", "Sekce „Komponenty“");
  if (name === "list") {
    assistant.list.value = {
      items: [
        { id: "c1", title: "Jak se připojím k firemní VPN?", available: true, lastActivityAt: new Date(Date.now() - 5 * 60_000).toISOString() },
        { id: "c2", title: null, available: false, lastActivityAt: new Date(Date.now() - 3 * 3_600_000).toISOString() },
        { id: "c3", title: "Kde najdu pravidla pro práci z domova?", available: true, lastActivityAt: new Date(Date.now() - 2 * 86_400_000).toISOString() },
      ], next: null, loading: false, error: "",
    };
    assistant.view.value = "list";
  }
}
watch(scene, seed, { immediate: true });
onBeforeUnmount(() => assistant.reset());
const frameWidth = computed(() => (narrow.value ? "360px" : "400px"));
</script>

<template>
  <SettingsSection title="AssistantPanel" description="components/assistant/AssistantPanel.vue (+ ConversationThread, AssistantComposer, ConversationList, AssistantPanelHeader, AssistantLauncher) — feature komponenta: skládá existující díly (OptionPills, SettingsList, AiAnswer, ConfirmDialog, ActionDisclosure) do asistenta ve WorkspaceSidePanel; vlastní stav je useAssistant. Kdy ji zvolit: asistent musí zůstat vedle čtené stránky a citace mají stránku posouvat. Nejbližší alternativy: AppDialog size=&quot;side&quot; je modální a stránku pod sebou zablokuje (Historie, Čtenost), karta Odpověď z wiki v omnisearch je jednorázová odpověď bez návazných otázek. Z karty se do panelu přechází přes Pokračovat v asistentovi. Vedle přepínače je malé tlačítko „Co asistent s nástroji umí“ (AssistantToolsInfo, ui/popover otevřený nad tlačítkem, Esc ho zavře a fokus se vrátí): seznam nástrojů, které server nabízí právě tomuto uživateli (GET /api/assistant/tools), po skupinách s krátkým popisem, čtecí a zapisující (jen navrhují, provede se po potvrzení) označené; načte se při prvním otevření a bez práva na nástroje se nevykreslí. Pro seznam v popoveru není nová komponenta, skládá se z existujícího ui/popover. Přepínač „S nástroji“ (D14) je ve skladateli vedle volby náročnosti, platí pro další otázky (proto přepínač a ne druhé tlačítko odeslání), výchozí je zapnuto a pamatuje se jako náročnost do odhlášení; zapnutý asistent si sám čte, co potřebuje, takže náročnost je zašedlá s vysvětlením. Bez schopnosti agent (scéna Bez práva) se nevykreslí a panel odpovídá jako dřív. Scéna Kroky běží ukazuje kroky nástrojů s českými slovesy, hotová odpověď je skládá do „Použité kroky ▸ n“ (InlineReveal, obdoba „Zdroje ▸ n“). Scény Návrh úpravy sekce, Přeformátování a Komponenta by se ztratila (409) ukazují kartu konceptu (WP-31): jeden řádek se stránkou a změněnými sekcemi jako slovní rozdíl HistoryChanges (jen Markdown, popisek místo verzí; komponenta je „⟦komponenta: …⟧“), po provedení „Otevřít v editoru“ (App.vue otevře stránku v režimu úprav, publikuje se zvlášť); přeformátování podle jiné stránky je jeden návrh atlas_replace_page, a když model vynechá značku komponenty, server ho odmítne 409 components_removed, karta to řekne a nic se neuloží. Scéna Uspořádání menu ukazuje návrh atlas_arrange_pages: celé levé menu prostoru jako jedna karta, řádek = jeden přesun (cesta před → po, u změny pořadí „(před …)“), přesuny se po potvrzení provedou postupně v jedné transakci, vše nebo nic; odškrtnutý přesun, na kterém závisí jiný, ten druhý shodí s důvodem a nepřesune se nic. Scéna Návrh přesunu (WP-40) ukazuje jednu stránku s cestou před (přeškrtnutě) → po a počtem podstránek, které se přesunou s ní; u přesunu do jiného prostoru karta řekne, že přístup převezme cílový prostor. Scéna Návrh publikace (WP-41, jen na výslovnou žádost v poslední zprávě) ukazuje verze, změněné sekce a popis změny a pod nimi nálezy kontroly před publikací jako řádky PublishFindings, takže nová sdílená komponenta nevzniká. Scény Návrh štítků, Provedeno, Konflikt 409 a 40 položek ukazují kartu návrhu (AssistantProposal.vue, soukromá komponenta panelu): složená z ImpactPreview, PageIcon, ui/checkbox a ResultBanner, řádek = stránka s ikonou a tagy před → po (přidaný tag +, odebraný přeškrtnutý), nahoře „Vybráno n z m“ a Vybrat vše / Zrušit výběr, seznam se posouvá uvnitř karty a Zahodit / Použít (slova P11) zůstávají pod ním (i pro 100 řádků a na 390 px). Karta patří do toku konverzace a smí počkat (proto ne dialog, který by blokoval čtení odpovědi). Fokus po zobrazení je na Zahodit, Escape jen vrátí fokus do pole otázky (zamítnutí je Zahodit; bez pole Esc projde panelu), nová zpráva kartu nahradí (read-only). Galerie rozhodnutí odpovídá sama, server nevolá. Scéna Nová konverzace ukazuje se zapnutými nástroji nejdřív úkoly pro wiki (se symbolem AI: převést kontakty, doplnit ikonu a tagy stránky, doplnit ikony a tagy prostoru, poskládat levé menu) a pod nimi otázky; úkol jen vyplní pole svým pokynem a zvolí rozsah, odeslání je na člověku. Tytéž úkoly nabízí paleta příkazů (Ctrl K, „>“) a tam se spustí rovnou; pisatelům prostoru, ne čtenářům (assistantEntries.ts wikiTasks). Panel nikdy neukáže nic z AI, když schopnost asistenta chybí; při výpadku s rozpracovanou výměnou zůstane s větou „Tahle funkce teď není k dispozici.“ a bez pole pro otázku.">
    <div class="asp-example">
      <OptionPills v-model="scene" label="Scéna" :options="scenes" />
      <label class="asp-toggle"><input v-model="narrow" type="checkbox" data-asp-narrow /> Úzký sloupec 360 px</label>
      <p v-if="editorOpened" class="asp-toggle" role="status" data-asp-editor>{{ editorOpened }}</p>
      <div class="asp-stage">
        <div class="asp-page" aria-hidden="true">
          <strong>Připojení k VPN</strong>
          <p>Stránka vedle panelu zůstává čitelná a ovladatelná. Citace v odpovědi ji posune na kotvu a sekce krátce zasvítí.</p>
          <p><AtlasIcon name="page" :size="14" /> Publikovaná stránka · čtení</p>
        </div>
        <aside class="asp-frame" :style="{ width: frameWidth }" aria-label="Ukázka panelu Asistent" data-asp-frame>
          <header class="asp-frame-header"><AssistantPanelHeader /><span class="asp-close" aria-hidden="true"><AtlasIcon name="close" :size="16" /></span></header>
          <div class="asp-frame-body"><AssistantPanel :key="scene" :agent="scene !== 'no-right'" :arrange="scene !== 'no-right'" :tool-groups="toolGroups" /></div>
        </aside>
      </div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.asp-example { display: grid; gap: 12px; }
.asp-toggle { display: flex; align-items: center; gap: 6px; color: var(--muted); font-size: 12.5px; }
.asp-stage { display: flex; align-items: stretch; gap: 0; border: 1px solid var(--line); border-radius: 12px; background: var(--canvas); overflow: hidden; }
.asp-page { flex: 1; min-width: 160px; padding: 16px; color: var(--text); font-size: 13px; }
.asp-page p { color: var(--muted); }
.asp-frame { flex: none; display: flex; flex-direction: column; height: 560px; max-width: 100%; border-left: 1px solid var(--line); background: var(--surface); color: var(--text); }
.asp-frame-header { display: flex; align-items: center; gap: 8px; padding: 10px 12px; border-bottom: 1px solid var(--line); font-weight: 650; }
.asp-close { display: grid; place-items: center; width: 28px; height: 28px; color: var(--muted); }
.asp-frame-body { flex: 1; min-height: 0; }
@media (max-width: 760px) { .asp-stage { flex-direction: column; } .asp-frame { width: 100% !important; border-left: 0; border-top: 1px solid var(--line); } }
</style>
