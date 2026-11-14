<script setup lang="ts">
import { onMounted, ref } from "vue";
import type { RevealTarget } from "../../../editor/revealTarget";
import { createSecretExamples } from "../../../assist/rules";
import type { PublicationCheck, PublicationCheckRun } from "../../../publishing/publication";
import { createPublicationChecks, linkFindings, localFindings, type LinkFinding } from "../../../publishing/publicationChecks";
import OptionPills from "../../settings/OptionPills.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import PublishControl from "../../publishing/PublishControl.vue";
import PublishFindings from "../../publishing/PublishFindings.vue";
import { useStoryboard } from "./storyboard";

// P5 "Před publikací" (plan §9, package E). Local data, no server: the real rules run over sample pages and the server part
// is a local stand-in with a shortened budget. Prototype: "Založení a publikace", moments 5 and 6 (the panel drops from
// the button, rows start as a spinner, the panel grows by each row, the icon draws itself, the row flashes, then the
// change summary and buttons unfold). Rules, not a model: type icons and a spinner, never the AI glyph or twinkle.
const root = ref<HTMLElement>();
const { sleep } = useStoryboard(root);

const text = (value: string) => ({ type: "text", text: value });
const rich = (...content: unknown[]) => JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content } });
// A sample password for the rule to find; it never leaves this page, like any value the check finds.
const sample = rich(
  { type: "paragraph", content: [text("V telefonu nainstalujte FortiClient a přihlaste se firemním účtem.")] },
  { type: "heading", attrs: { level: 2, headingId: "pristup" }, content: [text("Přístup")] },
  { type: "paragraph", content: [text("Testovací účet: mobil-test, heslo Mob!le2026x")] },
  { type: "heading", attrs: { level: 2, headingId: "postup" }, content: [text("Postup")] },
  { type: "paragraph", content: [text("Profil VPN nainstaluje Intune.")] },
  { type: "heading", attrs: { level: 2, headingId: "poznamky" }, content: [text("Poznámky")] },
);
const onlyEmptySection = rich({ type: "paragraph", content: [text("Úvod.")] }, { type: "heading", attrs: { level: 2 }, content: [text("Poznámky")] });
const clean = rich({ type: "paragraph", content: [text("Stránka bez nálezů.")] });
const links: LinkFinding[] = [
  { targetPageId: "0a1b2c3d-0000-0000-0000-000000000001", state: "unavailable", title: null },
  { targetPageId: "0a1b2c3d-0000-0000-0000-000000000002", state: "unpublished", title: "Staré VPN" },
];
// Examples never share the application's session memory of "Je to jen příklad".
const examples = createSecretExamples();

// The four findings and the information-only case, rendered as the panel's rows.
const all: PublicationCheck[] = [...localFindings(sample, { pageId: "gallery", examples }), ...linkFindings(links)];
const infoOnly = localFindings(onlyEmptySection, { pageId: "gallery", examples });
const staged = ref<PublicationCheck[]>(localFindings(sample, { pageId: "gallery", examples }));
const stagedRunning = ref(true);
onMounted(async () => {
  // The links answer a moment after the local rules and take the place of "Kontroluji…".
  await sleep(900);
  staged.value = all;
  stagedRunning.value = false;
});

// The live control: Publikovat saves (here: nothing), checks, and opens the panel only for findings.
type Scenario = "all" | "info" | "clean" | "silent";
const scenario = ref<Scenario>("all");
const scenarios = [
  { value: "all", label: "Heslo, odkazy, prázdná sekce", icon: "triangle-alert" },
  { value: "info", label: "Jen informace", icon: "info" },
  { value: "clean", label: "Nic nenalezeno", icon: "check" },
  { value: "silent", label: "Server neodpoví (rozpočet 400 ms)", icon: "clock" },
];
const status = ref("Koncept");
const revealed = ref("");
function runCheck(): PublicationCheckRun | null {
  const content = scenario.value === "all" ? sample : scenario.value === "info" ? onlyEmptySection : clean;
  return createPublicationChecks({
    page: () => ({ id: "gallery", version: 3, contentJson: content }),
    save: async () => true,
    examples,
    budgetMs: scenario.value === "silent" ? 400 : 1500,
    fetchCheck: async () => {
      if (scenario.value === "silent") return new Promise<never>(() => {});
      // Server latency, not motion: it stays 700 ms even when the gallery slows motion down, within the budget.
      await new Promise(resolve => setTimeout(resolve, 700));
      return scenario.value === "all" ? linkFindings(links) : [];
    },
  })();
}
async function publish(summary: string | null) {
  status.value = "Publikuji…";
  await sleep(400);
  status.value = summary ? `Publikováno · ${summary}` : "Publikováno";
  return true;
}
function describe(target: RevealTarget) {
  if (target.kind === "page") return "odkaz na stránku v textu";
  if (target.kind === "heading") return `nadpis${target.id ? ` #${target.id}` : ""}`;
  return `úsek v bloku ${target.path.join(".")}, znaky ${target.from}–${target.to}`;
}
</script>

<template>
  <SettingsSection
    title="PublishFindings"
    description="components/publishing/PublishFindings.vue — nálezy kontroly před publikací (P5) v panelu „Před publikací“ rozděleného tlačítka PublishControl. Proč nová komponenta: řádky jsou SettingsListRow se StatusBadge, nové je jen pevné pořadí (hesla, odkazy, prázdné sekce), řádek „Kontroluji…“ na místě odkazů a pohyb spinner → dokreslení ikony; ResultBanner hlásí výsledek hotové akce a SuggestionChip neuložený návrh hodnoty, ne nález. Volte ji jen uvnitř PublishControl; nic nezakazuje, Publikovat zůstává povolené. Pravidla, ne model: ikona typu a spinner, nikdy ikona AI."
  >
    <div ref="root" class="findings-example">
      <h4 class="findings-heading">Živě: Publikovat se zkontroluje a panel se otevře, jen když něco najde</h4>
      <OptionPills v-model="scenario" :options="scenarios" label="Obsah stránky" />
      <div class="findings-row">
        <PublishControl :prepare="async () => true" :check="runCheck" :publish="publish" status-selector=".findings-status" @reveal="revealed = describe($event)" />
        <span class="findings-status" role="status">{{ status }}</span>
      </div>
      <p class="findings-note">
        {{ revealed ? `Zobrazit → ${revealed} (v aplikaci se vybere v editoru, doroluje a dosvítí).` : "Zobrazit a Zpět k úpravám zavřou panel a ukážou místo v editoru; Escape = Zpět k úpravám." }}
        Bez nálezů se publikuje hned jako dnes. Když server do rozpočtu neodpoví, publikuje se jen s místními nálezy.
      </p>

      <div class="findings-grid">
        <div class="findings-cell">
          <span class="findings-caption">Všechny čtyři nálezy: odkazy dorazí ze serveru později</span>
          <div class="findings-frame" role="group" aria-label="Před publikací (ukázka)">
            <p class="findings-title">Před publikací</p>
            <PublishFindings :checks="staged" :running="stagedRunning" @reveal="revealed = describe($event.target!)" />
          </div>
        </div>
        <div class="findings-cell">
          <span class="findings-caption">Jen ℹ: potvrzení zní „Publikovat“</span>
          <div class="findings-frame" role="group" aria-label="Před publikací (jen informace)">
            <p class="findings-title">Před publikací</p>
            <PublishFindings :checks="infoOnly" />
          </div>
        </div>
        <div class="findings-cell findings-narrow">
          <span class="findings-caption">Úzký panel 320 px</span>
          <div class="findings-frame" role="group" aria-label="Před publikací (úzký)">
            <PublishFindings :checks="all" />
          </div>
        </div>
      </div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.findings-example { display: grid; gap: 14px; min-width: 0; }
.findings-heading { margin: 0; font-size: 13px; }
.findings-row { display: flex; flex-wrap: wrap; align-items: center; gap: 14px; }
.findings-status { color: var(--muted); font-size: 13px; }
.findings-note { margin: 0; color: var(--muted); font-size: 12.5px; }
.findings-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 16px; align-items: start; }
.findings-cell { display: grid; gap: 6px; min-width: 0; }
.findings-caption { color: var(--muted); font-size: 12px; }
/* The panel's surface, as the popover draws it, so the rows can be compared without opening it. */
.findings-frame { display: grid; gap: 8px; padding: 10px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); box-shadow: 0 6px 18px rgb(0 0 0 / .06); }
.findings-title { margin: 0; padding: 2px 6px 0; font-size: 13px; font-weight: 650; }
.findings-narrow .findings-frame { max-width: 320px; }
</style>
