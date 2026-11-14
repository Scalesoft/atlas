<script setup lang="ts">
import { provide, ref, watch, type Component } from "vue";
import { aiUsageLoaderKey } from "../../ai/quotaUsage";
import AtlasIcon from "../AtlasIcon.vue";
import AiGlyph from "../ai/AiGlyph.vue";
import { Button } from "../ui/button";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import { useGalleryMotion } from "./galleryMotion";

// #/gallery/ai: the visual and motion language of AI and suggestions (plan §0, §0a, §4 A1).
// Every package adds its examples as dev/ai/<Name>Example.vue; this page picks them up in file-name
// order, so nobody else edits the gallery. The motion switches are the gallery's own (its header, for every
// page); like the prototype's, switching one restarts every example below.
provide(aiUsageLoaderKey, async () => ({ enabled: true, limits: [
  { task: "search.answer", effective: 20, used: 3, remaining: 17, resetAt: "2026-10-07T00:00:00Z", source: "custom", origin: "group", groupId: "sample", groupName: "Vývoj" },
  { task: "chat.answer", effective: 50, used: 8, remaining: 42, resetAt: "2026-10-07T00:00:00Z", source: "custom", origin: "group", groupId: "sample", groupName: "Vývoj" },
  { task: "page.summary", effective: null, used: 2, remaining: null, resetAt: "2026-10-07T00:00:00Z", source: "unlimited", origin: "global", groupId: null, groupName: null },
] }));
const modules = import.meta.glob<{ default: Component }>("./ai/*Example.vue", { eager: true });
const examples = Object.keys(modules).sort().map((path) => ({
  name: path.slice(path.lastIndexOf("/") + 1, -".vue".length),
  component: modules[path]!.default,
}));

const { slow, reduced } = useGalleryMotion();
const run = ref(0);
watch([slow, reduced], () => run.value++);
</script>

<template>
  <SettingsPage title="AI a návrhy" description="Jeden vizuální a pohybový jazyk pro výstupy modelu i pro návrhy z pravidel. Příklady jde přehrát i zpomaleně a s omezeným pohybem. Místní data, žádné požadavky na server.">
    <!-- data-atlas-motion: copies that aiMotion flies (flyGhost) land in here and recompute the motion tokens. -->
    <div class="ai-gallery" data-atlas-motion>
      <div class="ai-gallery-controls" role="group" aria-label="Přehrávání ukázek">
        <Button size="sm" @click="run++"><AtlasIcon name="rotate-cw" :size="14" />Přehrát znovu</Button>
        <small>Zpomalit 3× a Omezený pohyb jsou v hlavičce galerie.</small>
      </div>

      <SettingsSection title="Pravidla" description="Kdy ikona AI, kdy ikona typu; kdy SuggestionChip, kdy značka editoru.">
        <ul class="ai-gallery-rules">
          <li><strong>Asistence je oddělitelná.</strong> Wiki se bez ní chová stejně. Návrhy v textu jsou volitelný modul editoru <code>@atlas/markdown/assist</code>, který Atlas načte líně, jen při zapnuté asistenci. Pravidla (<code>src/assist/rules</code>) na editoru nezávisí.</li>
          <li><strong>Jedna ikona AI.</strong> <AiGlyph :size="14" /> kreslí jen <code>AiGlyph</code>, a to jen u výstupu modelu a u vstupů, které modelovou úlohu spouštějí. Štítek „AI“ jen u textu z modelu.</li>
          <li><strong>Pravidla mají ikonu typu.</strong> Duplicity, značky v textu, kontrola před publikací, Odkazuje sem, pole Popis změny: stejný obrys, dosvit a pohyb, ale ikona <code>info</code>, <code>link</code>, <code>triangle-alert</code>, <code>map-pin</code>… a spinner místo twinkle.</li>
          <li><strong>Modelové úlohy jsou volitelné.</strong> AiGlyph a pracovní pohyb se zobrazí jen u dostupné modelové funkce. Pravidlové návrhy fungují samostatně i při výpadku AI.</li>
          <li><strong>SuggestionChip, nebo značka editoru.</strong> Mimo text stránky (pole, formuláře, panely) je návrh <code>SuggestionChip</code>. V textu stránky ho nese značka u bloku z modulu <code>assist</code>.</li>
          <li><strong>Pohyb je definovaný jednou.</strong> Komponenty volají <code>src/motion/aiMotion.ts</code> a třídy <code>styles/ai.css</code>; vlastní <code>@keyframes</code> ani délky nepíšou. Omezený pohyb nechá jen prolínání do 160 ms a smyčky zastaví; pracující znak AI jen dýchá průhledností.</li>
          <li><strong>Jeden stavový řádek.</strong> Když model pracuje, řekne to vždy <code>AiStatus</code>: znak ve stavu kroku, sloveso, co je známo, čas a Zastavit (jen kde úlohu jde opravdu zastavit). Ve vyhledávání XS, v kartě odpovědi S, v asistentovi M s kroky.</li>
          <li><strong>Jedna cesta do asistenta.</strong> Položka Asistent v menu a <kbd>Ctrl J</kbd>, režim Zeptat se v hledání (<kbd>?</kbd>), Zeptat se na tuto stránku (jen když asistent teď umí odpovědět, jako v paletě) a lišta nad vybraným textem (Zeptat se · Vysvětlit · Shrnout). Všechny otevřou tentýž panel.</li>
        </ul>
      </SettingsSection>

      <div v-for="example in examples" :key="`${example.name}-${run}`" class="ai-gallery-example" :data-ai-example="example.name">
        <component :is="example.component" />
      </div>
    </div>
  </SettingsPage>
</template>

<style scoped>
.ai-gallery { display: grid; gap: 14px; min-width: 0; }
.ai-gallery-controls { position: sticky; top: 8px; z-index: 5; display: flex; flex-wrap: wrap; align-items: center; gap: 10px 18px; justify-self: start; padding: 8px 10px 8px 14px; border: 1px solid var(--line); border-radius: 999px; background: var(--surface); box-shadow: 0 6px 18px rgb(0 0 0 / .06); }
.ai-gallery-controls small { color: var(--muted); font-size: 12px; }
.ai-gallery-rules { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 10px 24px; margin: 0; padding: 0; list-style: none; font-size: 13px; line-height: 1.55; }
.ai-gallery-rules li { color: var(--muted); }
.ai-gallery-rules strong { color: var(--text); }
.ai-gallery-rules code { font-size: 12px; }
.ai-gallery-rules :deep(.ai-glyph) { vertical-align: -2px; }
.ai-gallery-example { min-width: 0; }
</style>
