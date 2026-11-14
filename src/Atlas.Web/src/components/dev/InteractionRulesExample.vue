<script setup lang="ts">
import { computed, ref } from "vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import SearchField from "../settings/SearchField.vue";
import { Button } from "../ui/button";
import { useToast } from "../../composables/useToast";
import { interactionAreas, interactionChecks, interactionDeviations, interactionRules, interactionSharedParts, type InteractionArea } from "../../contracts/interaction";

// The rules in short and the live list of what the contract suites still accept: the deviations are the suites' own data, so
// the list shrinks here when an alignment area deletes a row (P16). Local data only.
const areas = Object.keys(interactionAreas) as InteractionArea[];
const query = ref("");
const fold = (text: string) => text.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase();
const rows = computed(() => areas.map(area => ({ area, title: interactionAreas[area], all: interactionDeviations[area],
  shown: interactionDeviations[area].filter(row => !query.value.trim() || fold(`${row.element} ${row.check} ${row.reason} ${row.ref ?? ""}`).includes(fold(query.value.trim()))) })));
const total = computed(() => areas.reduce((sum, area) => sum + interactionDeviations[area].length, 0));
const checkText = (check: string) => (interactionChecks as Record<string, { text: string }>)[check]?.text ?? check;

// The one notification path with „Zpět“ (P6), live.
const toaster = useToast();
const removed = ref(false);
// The control stays the same element (it becomes „Obnovit“), so the keyboard stays on it (P7). Ctrl+Z is the history of the editor and boards, not of this demo (P6): Zpět is on the toast.
function toggleSample() {
  if (removed.value) { removed.value = false; return; }
  removed.value = true;
  toaster.success("Ukázková položka je v koši.", { undo: () => { removed.value = false; } });
}
</script>

<template>
  <SettingsPage title="Pravidla ovládání" description="docs/interaction-rules.md v kostce, sdílené díly, které pravidla vyžadují, a živý seznam odchylek, které kontraktní testy (P16) zatím přijímají. Každá odchylka je úkol oblasti sjednocení: řádek zmizí, až prvek pravidlo splní.">
    <SettingsSection title="Pravidla P1–P17" description="Celé znění a rozhodnutí vlastníka D1–D3 jsou v docs/interaction-rules.md.">
      <dl class="rules-list">
        <div v-for="rule in interactionRules" :key="rule.id" class="rules-item">
          <dt><span class="rules-id">{{ rule.id }}</span> {{ rule.title }}</dt>
          <dd>{{ rule.text }}</dd>
        </div>
      </dl>
    </SettingsSection>

    <SettingsSection title="Sdílené díly" description="Komponenty se skládají z těchto dílů (P16); vlastní varianta potřebuje důvod v galerii.">
      <ul class="rules-parts">
        <li v-for="part in interactionSharedParts" :key="part.name"><strong>{{ part.name }}</strong> <code>{{ part.where }}</code><span>{{ part.text }}</span></li>
      </ul>
      <div class="rules-demo">
        <div class="rules-states" aria-label="Tokeny stavů">
          <span class="rules-state" data-state="hover">najetí</span>
          <span class="rules-state" data-state="active-row">řádek, který vezme Enter</span>
          <span class="rules-state" data-state="selected">vybráno</span>
          <span class="rules-state" data-state="editing">upravuje se</span>
          <button type="button" class="rules-state" data-state="focus">fokus (Tab)</button>
          <label class="rules-field">fokus pole <input type="text" aria-label="Ukázkové pole" placeholder="napište" /></label>
          <span class="rules-state" data-state="error">chyba</span>
          <span class="rules-state" data-state="unresolved">nerozhodnuto</span>
        </div>
        <div class="st-actions">
          <Button type="button" variant="outline" @click="toggleSample">{{ removed ? "Obnovit ukázku" : "Smazat ukázku (oznámení se Zpět)" }}</Button>
        </div>
      </div>
    </SettingsSection>

    <SettingsSection :title="`Odchylky dnes (${total})`" description="Z kontraktních testů: editor src/Atlas.Markdown/src/contracts/interaction-deviations.json, ostatní src/Atlas.Web/src/contracts/deviations/*.json. Odkaz „A#1“ míří do soupisu docs/interaction-rules-inventory-2026-10-30.md.">
      <SearchField v-model="query" label="Hledat v odchylkách" placeholder="Prvek, pravidlo nebo důvod" />
      <section v-for="group in rows" :key="group.area" class="rules-area" :aria-label="group.title">
        <h3>{{ group.area }} {{ group.title }} <span class="st-muted">{{ group.shown.length }} z {{ group.all.length }}</span></h3>
        <p v-if="!group.all.length" class="st-muted">Bez odchylek.</p>
        <div v-else-if="group.shown.length" class="rules-scroll"><table class="rules-table">
          <thead><tr><th scope="col">Prvek</th><th scope="col">Pravidlo</th><th scope="col">Proč dnes ne</th><th scope="col">Soupis</th></tr></thead>
          <tbody>
            <tr v-for="row in group.shown" :key="`${row.element} ${row.check}`">
              <td><code>{{ row.element }}</code></td>
              <td :title="checkText(row.check)"><code>{{ row.check }}</code></td>
              <td>{{ row.reason }}</td>
              <td>{{ row.ref ?? "–" }}</td>
            </tr>
          </tbody>
        </table></div>
        <p v-else class="st-muted">Nic neodpovídá hledání.</p>
      </section>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.rules-list { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 320px), 1fr)); gap: 12px 24px; margin: 0; }
.rules-item dt { font-weight: 600; }
.rules-item dd { margin: 2px 0 0; color: var(--muted, var(--text-dim)); }
.rules-id { display: inline-block; min-width: 2.6em; color: var(--accent); font-variant-numeric: tabular-nums; }
.rules-parts { display: grid; gap: 6px; margin: 0; padding: 0; list-style: none; }
.rules-parts li { display: flex; flex-wrap: wrap; gap: 4px 8px; align-items: baseline; }
.rules-parts span { color: var(--muted, var(--text-dim)); }
.rules-demo { display: grid; gap: 12px; margin-top: 16px; }
.rules-states { display: flex; flex-wrap: wrap; gap: 10px; }
.rules-state { padding: 4px 10px; border: 1px solid var(--line); border-radius: var(--radius-sm, 6px); background: none; color: inherit; font: inherit; }
.rules-state[data-state="hover"] { background: var(--state-hover-bg); }
.rules-state[data-state="active-row"] { background: var(--state-active-row-bg); box-shadow: var(--state-active-row-mark); }
.rules-state[data-state="selected"] { outline: var(--state-selected-outline); outline-offset: 2px; }
.rules-state[data-state="editing"] { border-color: var(--state-editing); }
.rules-state[data-state="focus"]:focus-visible, .rules-field:focus-within { outline: var(--state-focus-ring); outline-offset: var(--state-focus-offset); }
.rules-field { padding: 4px 10px; border: 1px solid var(--input-line, var(--line)); border-radius: var(--radius-sm, 6px); }
.rules-field input { border: 0; outline: 0; background: none; color: inherit; font: inherit; }
.rules-state[data-state="error"] { border-color: var(--state-error); color: var(--state-error); }
.rules-state[data-state="unresolved"] { border: var(--state-unresolved-border); color: var(--state-unresolved); }
.rules-area { display: grid; gap: 8px; margin-top: 16px; min-width: 0; container-type: inline-size; }
.rules-scroll { min-width: 0; overflow-x: auto; }
.rules-area h3 { margin: 0; font-size: 1rem; overflow-wrap: anywhere; }
.rules-table { width: 100%; border-collapse: collapse; font-size: 13px; }
.rules-table :is(th, td) { padding: 6px 8px; border-bottom: 1px solid var(--line); text-align: left; vertical-align: top; }
.rules-table td { overflow-wrap: anywhere; }
@container (max-width: 640px) { .rules-table th:nth-child(4), .rules-table td:nth-child(4) { display: none; } }
</style>
