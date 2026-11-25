<script setup lang="ts">
import { reactive, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import AiGlyph, { AI_GLYPH_SIZES, type AiGlyphSize, type AiGlyphState } from "../../ai/AiGlyph.vue";
import { AI_GLYPH_MOVES } from "../../ai/aiGlyphBusy";
import SettingsSection from "../../settings/SettingsSection.vue";
import SettingsNav from "../../settings/SettingsNav.vue";
import SettingsPage from "../../settings/SettingsPage.vue";
import SuggestionChip from "../../settings/SuggestionChip.vue";

// Board „AI znak – velikosti, stavy a průběh“: one drawing, six sizes, eight states. The cards are buttons, so a click
// plays a one-off state again and pointing at „Připraveno“ shows what a launcher does on hover or focus.
const sizes: { name: AiGlyphSize; use: string }[] = [
  { name: "xs", use: "vyhledávací pole, čip návrhu, štítek „AI“" },
  { name: "s", use: "tlačítka, menu, položka Asistent, odpověď" },
  { name: "m", use: "hlavička panelu asistenta, kontrola publikace" },
  { name: "l", use: "odpověď v asistentovi, souhrn stránky" },
  { name: "xl", use: "prázdný asistent, první spuštění" },
  { name: "hero", use: "uvítání AI v nastavení, onboarding" },
];
const states: { state: AiGlyphState; name: string; what: string; where: string }[] = [
  { state: "idle", name: "Klid", what: "Statický znak. AI je k dispozici.", where: "menu, tlačítka, hotová odpověď" },
  { state: "ready", name: "Připraveno", what: "Najetí nebo fokus: hvězda se zhoupne a malé ji doprovodí.", where: "vstupy do asistenta, „Zeptat se“" },
  { state: "thinking", name: "Přemýšlí", what: "Malé hvězdy obíhají, velká se kolébá v dýchající záři.", where: "čekání na model, rozbor dotazu" },
  { state: "searching", name: "Hledá ve zdrojích", what: "Kolem znaku se točí přerušovaný kruh.", where: "významové hledání, hledání podkladů" },
  { state: "writing", name: "Píše", what: "Hvězda pulzuje, malé jiskry poskakují v rytmu psaní.", where: "psaní odpovědi a shrnutí" },
  { state: "done", name: "Hotovo", what: "Hvězdy se slijí do jedné, krátké „cvak“ a fajfka.", where: "právě dokončená odpověď" },
  { state: "suggest", name: "Návrh pro vás", what: "Jednou se rozzáří jemná aura.", where: "návrh štítku, popisu změny" },
  { state: "unavailable", name: "Nedostupné", what: "Šedý znak a oranžová tečka. Nic se nehýbe.", where: "výpadek modelu, vyčerpaný limit" },
];
const replay = reactive<Record<string, number>>({});
const sizeState = ref<AiGlyphState>('thinking');
const play = (state: string) => { replay[state] = (replay[state] ?? 0) + 1; };
// Busy variants: the demo cycles as in the application (move unset); a variant button or „Zamíchat“ pins one.
const busyState = ref<AiGlyphState>("thinking");
const busyMove = ref<number>();
const shuffle = () => { let next = busyMove.value; while (next === busyMove.value) next = 1 + Math.floor(Math.random() * AI_GLYPH_MOVES.length); busyMove.value = next; };
const label = (name: AiGlyphSize) => (name === "hero" ? "Hero" : name.toUpperCase());
</script>

<template>
  <SettingsSection title="AiGlyph: velikosti a stavy" description="components/ai/AiGlyph.vue — jediná ikona AI v Atlasu: kresba sparkles z registru ikon editoru v inkoustu AI (--ai-ink, akcent posunutý k indigu; jeden token, takže jde později přepnout), skrytá před čtečkou (význam nese text vedle). Proč ne AtlasIcon: ten kreslí libovolnou ikonu; AiGlyph svazuje tuto jednu ikonu s pravidlem, kdy se smí ukázat, se šesti velikostmi a se stavem práce. Pohyb je jen v styles/ai.css na tokenech motion.css; s omezeným pohybem se nic nehýbe a pracovní stavy jen dýchají průhledností. Klepnutím na kartu stavu se jednorázový pohyb přehraje znovu. Při práci znak střídá náhodně 24 variant pohybu hvězd (components/ai/aiGlyphBusy.ts).">
    <div class="ai-glyph-example">
      <div class="age-chips" aria-label="Časování">
        <span>smyčky 1,6 s · reveal 420 · gather 520 · solidify 360 ms</span>
        <span>omezený pohyb: jen dýchání průhlednosti</span>
      </div>

      <h4 class="age-heading">Velikosti</h4>
      <label class="age-preview-state">Pohyb ve skutečné velikosti
        <select v-model="sizeState" aria-label="Pohyb ve skutečné velikosti"><option v-for="item in states" :key="item.state" :value="item.state">{{ item.name }}</option></select>
      </label>
      <div class="age-sizes">
        <div v-for="size in sizes" :key="size.name" class="age-size" :data-glyph-size="size.name">
          <span class="age-size-glyph"><AiGlyph :key="sizeState" :size="size.name" :state="sizeState" /></span>
          <strong>{{ label(size.name) }} · {{ AI_GLYPH_SIZES[size.name] }} px</strong>
          <small>{{ size.use }}</small>
        </div>
      </div>

      <h4 class="age-heading">Stavy – co znak dělá</h4>
      <div class="age-states">
        <button v-for="item in states" :key="item.state" type="button" class="age-state" :data-glyph-state="item.state" :aria-label="`${item.name}: přehrát znovu`" @click="play(item.state)">
          <span class="age-tile"><AiGlyph :key="replay[item.state] ?? 0" :state="item.state" :size="40" /></span>
          <span class="age-copy">
            <strong>{{ item.name }}<span v-if="item.state === 'writing'" class="ai-status-dots age-dots" aria-hidden="true"><i /><i /><i /></span></strong>
            <span>{{ item.what }}</span>
            <small>{{ item.where }}</small>
          </span>
        </button>
      </div>

      <h4 class="age-heading">Když AI pracuje – střídání variant</h4>
      <p class="age-note">Pracující znak nejdřív odehraje smyčku svého stavu a pak na konci každé smyčky přihodí jinou z {{ AI_GLYPH_MOVES.length }} variant hvězd, náhodně a nikdy dvakrát po sobě, až do výsledku (Hotovo). Varianty jsou v líně načteném styles/aiBusy.css, končí v klidu, takže na sebe navazují bez skoku; s omezeným pohybem znak jen dýchá průhledností.</p>
      <div class="age-busy">
        <div class="age-busy-stage">
          <span class="age-busy-glyphs">
            <AiGlyph v-for="size in (['s', 'l', 'hero'] as const)" :key="`${size}-${busyState}`" :size="size" :state="busyState" :move="busyMove" />
          </span>
          <label class="age-preview-state">Stav
            <select v-model="busyState" aria-label="Stav pracujícího znaku"><option v-for="item in states.filter((entry) => ['thinking', 'searching', 'writing'].includes(entry.state))" :key="item.state" :value="item.state">{{ item.name }}</option></select>
          </label>
          <span class="age-busy-actions">
            <Button type="button" size="xs" :variant="busyMove === undefined ? 'secondary' : 'outline'" :aria-pressed="busyMove === undefined" @click="busyMove = undefined">Náhodně jako v aplikaci</Button>
            <Button type="button" size="xs" variant="outline" @click="shuffle">Zamíchat</Button>
          </span>
        </div>
        <div class="age-moves" aria-label="Varianty">
          <Button v-for="(name, index) in AI_GLYPH_MOVES" :key="name" type="button" size="xs" :variant="busyMove === index + 1 ? 'secondary' : 'outline'" :aria-pressed="busyMove === index + 1" @click="busyMove = index + 1">{{ index + 1 }}. {{ name }}</Button>
        </div>
      </div>

      <h4 class="age-heading">Kdy znak, kdy ikona typu</h4>
      <div class="age-rule">
        <div class="age-case">
          <span class="age-caption is-yes"><AtlasIcon name="check" :size="14" />Ano: výstup modelu a vstupy modelové úlohy</span>
          <p>Odpověď, shrnutí, navržený popis změny, úprava výběru; Asistent, Shrnout, Zeptat se, AI ve výběru, Navrhnout. Štítek „AI“ jen u textu z modelu.</p>
          <SuggestionChip label="Návrh štítku" reason="Model navrhl štítek podle obsahu stránky (fáze 3, P4).">
            <template #glyph><AiGlyph size="xs" state="suggest" /></template>
            #vpn
          </SuggestionChip>
        </div>
        <div class="age-case">
          <span class="age-caption is-no"><AtlasIcon name="close" :size="14" />Ne: návrhy z pravidel a výsledky hledání</span>
          <p>Duplicity, značky v textu, kontrola před publikací, Odkazuje sem, výsledky a pasáže hledání, zdroje a citace: stejný vizuální jazyk, ale ikona typu a spinner místo pohybu znaku.</p>
          <SuggestionChip label="Návrh umístění" icon="map-pin" reason="Podobné stránky o VPN jsou v sekci Síť (fáze 2, P2).">IT a infrastruktura › Síť › VPN</SuggestionChip>
          <span class="age-line"><AtlasIcon name="loader-circle" :size="16" class="atlas-spinner" />Kontroluji…</span>
        </div>
        <div class="age-case">
          <span class="age-caption">Totožnost AI v nastavení</span>
          <SettingsNav :groups="[{ id: 'ai', items: [{ path: 'gallery/ai', label: 'AI', icon: 'ai' }] }]" label="Ukázka navigace AI" current-path="gallery/ai" />
          <SettingsPage title="AI" icon="ai" description="Stejná ikona jako u asistenta; technické volby mají ikonu svého typu." />
        </div>
      </div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.ai-glyph-example { display: grid; gap: 12px; min-width: 0; }
.age-heading { margin: 8px 0 0; color: var(--muted); font-size: 12px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; }
.age-preview-state { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; color: var(--muted); font-size: 13px; }
.age-preview-state select { max-width: 240px; }
.age-sizes { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); overflow: hidden; border: 1px solid var(--line); border-radius: 14px; }
.age-size { display: flex; flex-direction: column; align-items: center; justify-content: flex-end; gap: 10px; min-height: 150px; padding: 14px; border-right: 1px solid var(--refresh-soft-line, var(--line)); text-align: center; }
.age-size:last-child { border-right: 0; }
.age-size-glyph { display: flex; flex: 1; align-items: center; justify-content: center; }
.age-size strong { font-size: 14px; font-weight: 600; }
.age-size small { color: var(--muted); font-size: 12px; }
.age-states { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; }
.age-state { display: flex; gap: 14px; padding: 14px; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); color: var(--text); font: inherit; text-align: left; cursor: pointer; transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.age-state:hover { border-color: var(--ai-line-strong); }
.age-state:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.age-tile { display: grid; flex: none; place-items: center; width: 64px; height: 64px; border-radius: 16px; background: var(--ai-wash); }
.age-copy { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
.age-copy strong { display: inline-flex; align-items: center; gap: 8px; font-weight: 650; }
.age-copy > span { font-size: 13px; }
.age-copy small { color: var(--muted); font-size: 12px; }
.age-dots { display: inline-flex; gap: 3px; }
.age-dots > i { width: 4px; height: 4px; border-radius: 2px; background: var(--ai-ink); }
.age-note { margin: 0; color: var(--muted); font-size: 13px; }
.age-busy { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.4fr); gap: 12px; }
.age-busy-stage { display: grid; justify-items: start; gap: 12px; padding: 14px; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); }
.age-busy-glyphs { display: flex; align-items: center; gap: 28px; min-height: 96px; padding: 0 12px; }
.age-busy-actions, .age-moves { display: flex; flex-wrap: wrap; align-content: start; gap: 6px; }
.age-rule { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 12px; }
.age-case { display: grid; align-content: start; gap: 8px; min-width: 0; padding: 12px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); }
.age-caption { display: inline-flex; align-items: center; gap: 6px; color: var(--muted); font-size: 12px; font-weight: 600; }
.age-caption.is-yes svg { color: var(--success); }
.age-caption.is-no svg { color: var(--error); }
.age-case p { margin: 0; color: var(--muted); font-size: 12.5px; }
.age-case :deep(.suggestion-chip) { justify-self: start; }
.age-line { display: inline-flex; align-items: center; gap: 8px; color: var(--muted); font-size: 13px; }
@media (max-width: 1100px) { .age-busy { grid-template-columns: minmax(0, 1fr); } .age-states { grid-template-columns: repeat(2, minmax(0, 1fr)); } .age-sizes { grid-template-columns: repeat(3, minmax(0, 1fr)); } .age-size:nth-child(3n) { border-right: 0; } }
@media (max-width: 620px) { .age-states { grid-template-columns: minmax(0, 1fr); } .age-sizes { grid-template-columns: repeat(2, minmax(0, 1fr)); } .age-size:nth-child(3n) { border-right: 1px solid var(--refresh-soft-line, var(--line)); } .age-size:nth-child(2n) { border-right: 0; } }
</style>
