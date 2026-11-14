<script setup lang="ts">
import { nextTick, onMounted, reactive, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import AiGlyph from "../../ai/AiGlyph.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import SuggestionChip from "../../settings/SuggestionChip.vue";
import { FEEDBACK_HOLD_MS, flyGhost, foldOut, markerIn, morphInto, relocate, revealSentences, revealWave, rippleOnce, RISE_IN_ROWS, riseIn, solidify, unfoldFrom } from "../../../motion/aiMotion";
import { pulseMatch } from "../../../editor/pageSearch";
import { morphSearchPanel } from "../../../search/searchMorph";
import { useStoryboard } from "./storyboard";

// The nine tiles of the prototype's scene "Pohybový systém", each played by the aiMotion primitive
// that components use (no motion of its own here). Compare them with the prototype side by side;
// the page's switches slow them down 3× or force reduced motion. Click a tile to replay it.
type Kind = "marker" | "solid" | "gather" | "work" | "reveal" | "accept" | "dismiss" | "move" | "warn";
const tiles: { name: string; spec: string; text: string; kind: Kind; primitive: string }[] = [
  { name: "Návrh se objeví", spec: "280 ms · ease-out · rotace −140° → 0", text: "Přerušovaný kroužek se roztočí na místo. Nic se neposune a nevezme fokus.", kind: "marker", primitive: "markerIn" },
  { name: "Spuštění", spec: "360 ms · ease-in-out · čárky 6/5 → 11/0", text: "Čárky obrysu se spojí v plnou čáru: návrh se změnil v běžící úlohu.", kind: "solid", primitive: "solidify" },
  { name: "Shromáždění zdrojů", spec: "520 ms · ease-in-out · odstup 90 ms", text: "Zdroje přeletí do odpovědi po oblouku. Ukazuje, z čeho odpověď vzniká.", kind: "gather", primitive: "flyGhost" },
  { name: "Pracuji", spec: "smyčka 1,6 s · jediná povolená smyčka", text: "Jiskra se jemně pootočí, popisek přejede lesk. Zastaví se, jakmile je výsledek.", kind: "work", primitive: "AiGlyph working · .atlas-ai-shimmer" },
  { name: "Odhalení", spec: "420 ms · blur 4 px → 0 · rise 6 px · odstup 130 ms", text: "Hotový a ověřený text se odhalí po větách. Nejde o streamování tokenů.", kind: "reveal", primitive: "revealSentences" },
  { name: "Přijetí a dosvit", spec: "380 ms morph + 1,6 s dosvit", text: "Text se smrskne do komponenty a místo krátce dosvítí. Ctrl+Z vrátí.", kind: "accept", primitive: "morphInto · glow" },
  { name: "Zahození", spec: "160 ms · scale .6 · fade", text: "Návrh zmizí rychle a bez stopy. Stejný návrh se v relaci nevrátí.", kind: "dismiss", primitive: "SuggestionChip · dismissOut" },
  { name: "Přemístění", spec: "380 ms · ease-in-out", text: "Karta odpovědi se přemístí do panelu Asistent; obsah se nenačítá znovu.", kind: "move", primitive: "relocate" },
  { name: "Varování", spec: "600 ms · jedno zvlnění", text: "Riziko (heslo v textu) dostane jantarovou značku a jedno zvlnění. Žádné blikání.", kind: "warn", primitive: "markerIn · rippleOnce" },
];
const rules = [
  "Pohyb nikdy nezdrží psaní ani výsledky hledání.",
  "Jediná smyčka je „Pracuji“ a končí s výsledkem.",
  "Přerušovaný obrys vždy znamená „návrh, zatím neuložený“.",
  "Každá přijatá změna od AI dosvítí na místě, kde se stala.",
  "Fokus se při objevení návrhu ani odpovědi nepřesouvá.",
  "Žádné duhové gradienty a pulzující tlačítka. AI nemá vlastní barvu.",
];

// The foundation of the whole UI (motion unification plan §1.2, package M0): what M1–M4 move the shell,
// settings and brand onto. Compare with the prototype's scene "Hledání s odpovědí" (moment 1, the rows of
// setRows()), its menuIn/menuOut and toast(), and its loops (.spin, .src.busy .dot, shimmer).
type FoundationKind = "search" | "page" | "menu" | "toast" | "work" | "defer" | "wave" | "loading" | "rise-from" | "pulse";
const foundation: { name: string; spec: string; text: string; kind: FoundationKind; primitive: string }[] = [
  { name: "Otevření hledání", spec: "200 ms · výřez ze spouštěče · řádky 8 px / 200 ms / odstup 30 ms · zavření 160 ms · 8 px a .98", text: "Panel vyroste z pole, které ho otevřelo; výsledky přijedou po řádcích. Otevírá i zavírá se ve stejném čase jako dialogy.", kind: "search", primitive: "unfoldFrom · riseIn(RISE_IN_ROWS) · foldOut" },
  { name: "Hledání na celou stránku", spec: "200 ms · výřez z okna na celou stránku · zpět 160 ms · obsah se nezmenšuje", text: "Ctrl+Shift+Enter nebo tlačítko vedle Filtrů přepne totéž hledání mezi oknem a celou stránkou (adresa ?searchView=page): dotaz, filtry, výsledky, výběr i posun zůstanou. Okno je na rychlý skok, celá stránka na procházení dlouhého seznamu; výsledky se v obou donačítají při posunu. Esc hledání zavře, Zpět z otevřeného výsledku se vrátí do stejného pohledu. Pod 680 px je okno už celá obrazovka, přepínač se skryje.", kind: "page", primitive: "search/searchMorph.ts · morphSearchPanel" },
  { name: "Nabídka", spec: "160 ms · scale .94 · −4 px · ven 120 ms · .97", text: "Nabídka a popover vyrostou ze spouštěče. Bublina nápovědy jen prolne.", kind: "menu", primitive: "CSS atlas-menu-in / atlas-menu-out" },
  { name: "Oznámení", spec: "200 ms · +10 px · drží 1,6 s · ven 160 ms · +6 px", text: "Krátké potvrzení vyjede zespodu, chvíli zůstane a zase sjede.", kind: "toast", primitive: "CSS atlas-toast-in / -out · FEEDBACK_HOLD_MS" },
  { name: "Pracuje", spec: "spin 800 ms · puls 1 s · smyčka 1,6 s", text: "Smyčky jen dokud něco běží. Při omezeném pohybu stojí.", kind: "work", primitive: ".atlas-spinner · .atlas-pulse · --atlas-motion-loop" },
  { name: "Odklad", spec: "150 ms odklad · pak 200 ms prolnutí", text: "Průběh se ukáže až po odkladu: rychlá odpověď (100 ms) nebliká, pomalá (900 ms) ano.", kind: "defer", primitive: "--atlas-motion-defer" },
  { name: "Vlna stránky", spec: "řádky 8 px / 200 ms / odstup 30 ms · nejvýš 20 řádků", text: "Stránka se při prvním vykreslení odhalí shora dolů: bloky v pohledu vyjedou po řádcích, ty pod ním se vykreslí hned. Totéž předání do editace.", kind: "wave", primitive: "revealWave" },
  { name: "Načítání editoru", spec: "pás 40 vh · smyčka 1,6 s po odkladu 150 ms · omezený pohyb: stojí slabý tón", text: "Než živý editor převezme stránku, přejíždí po ní slabý pás světla. Jen transform, takže běží i během dlouhých úloh editoru.", kind: "loading", primitive: "DocumentEditor .document-loading-wave · atlas-loading-wave" },
  { name: "Zjevení z místa", spec: "riseIn from .6 · rise false · 200 ms / odstup 30 ms", text: "Řádky, které už na obrazovce jsou, se jen dosvětlí z 0,6 bez posunu (posun dělá FLIP). Vlevo výchozí riseIn, vpravo s from a rise.", kind: "rise-from", primitive: "riseIn({ from, rise })" },
  { name: "Pulz skoku", spec: "700 ms · prstenec scale 1.35 → 1 · fade", text: "Hledat ve stránce: po doskoku na výsledek se kolem něj jednou stáhne prstenec. Při omezeném pohybu jen prolne.", kind: "pulse", primitive: "editor/pageSearch.ts pulseMatch" },
];

const root = ref<HTMLElement>();
const { sleep, settle } = useStoryboard(root);
const runs = reactive(tiles.map(() => 0));
const minis: (HTMLElement | undefined)[] = [];
const foundationRuns = reactive(foundation.map(() => 0));
const foundationMinis: (HTMLElement | undefined)[] = [];
const menuState = ref<"open" | "closed" | "">("");
const toastState = ref<"in" | "out" | "">("");
const deferState = reactive({ label: "", busy: false });

async function playFoundation(index: number) {
  foundationRuns[index]!++;
  await nextTick();
  const mini = foundationMinis[index];
  if (!mini) return;
  switch (foundation[index]!.kind) {
    case "search": {
      const panel = mini.querySelector<HTMLElement>(".ai-ex-search-panel")!;
      await sleep(400);
      await settle(unfoldFrom(panel, mini.querySelector(".ai-ex-search-field")));
      await settle(riseIn(panel.querySelectorAll(".ai-ex-search-row"), RISE_IN_ROWS));
      await sleep(1400);
      await settle(foldOut(panel));
      panel.hidden = true;
      break;
    }
    case "page": {
      // The same switch as the search in App.vue: the attribute changes the layout, morphSearchPanel plays the change.
      const panel = mini.querySelector<HTMLElement>(".ai-ex-page-panel")!;
      const view = (value: "dialog" | "page") => async () => { panel.setAttribute("data-search-view", value); };
      await sleep(400);
      await morphSearchPanel(panel, "page", view("page"));
      await sleep(1400);
      await morphSearchPanel(panel, "dialog", view("dialog"));
      break;
    }
    case "menu":
      menuState.value = "";
      await sleep(400);
      menuState.value = "open";
      await sleep(1400);
      menuState.value = "closed";
      await sleep(300);
      menuState.value = "";
      break;
    case "toast":
      toastState.value = "";
      await sleep(400);
      toastState.value = "in";
      await sleep(FEEDBACK_HOLD_MS);
      toastState.value = "out";
      await sleep(300);
      toastState.value = "";
      break;
    case "defer":
      deferState.label = "Rychlá odpověď (100 ms)";
      deferState.busy = true;
      await sleep(100);
      deferState.busy = false;
      await sleep(900);
      deferState.label = "Pomalá odpověď (900 ms)";
      deferState.busy = true;
      await sleep(900);
      deferState.busy = false;
      deferState.label = "Hotovo";
      break;
    case "wave":
      await sleep(300);
      revealWave(mini.querySelector(".ai-ex-wave"));
      break;
    case "rise-from":
      await sleep(300);
      riseIn(mini.querySelectorAll(".ai-ex-rise-plain > span"), RISE_IN_ROWS);
      riseIn(mini.querySelectorAll(".ai-ex-rise-from > span"), { ...RISE_IN_ROWS, from: 0.6, rise: false });
      break;
    case "pulse": {
      await sleep(400);
      const range = document.createRange();
      range.selectNodeContents(mini.querySelector(".ai-ex-match")!);
      pulseMatch(range);
      break;
    }
    case "work":
    case "loading":
      break;
  }
}
function replayFoundationFromTile(event: MouseEvent, index: number) {
  if ((event.target as Element).closest("button")) return;
  void playFoundation(index);
}

async function play(index: number) {
  runs[index]!++;
  await nextTick();
  const mini = minis[index];
  if (!mini) return;
  const find = <T extends Element = HTMLElement>(selector: string) => mini.querySelector<T & Element>(selector)!;
  switch (tiles[index]!.kind) {
    case "marker":
      markerIn(find(".ai-ex-ring"), find(".ai-ex-marker-icon"));
      break;
    case "solid":
      await sleep(600);
      find(".ai-ex-card").classList.add("is-solid");
      solidify(find<SVGRectElement>("rect"));
      break;
    case "gather": {
      const slots = [...mini.querySelectorAll<HTMLElement>(".ai-ex-slot")];
      for (const [n, source] of [...mini.querySelectorAll(".ai-ex-source")].entries()) {
        slots[n]!.style.visibility = "";
        flyGhost(source, slots[n]!, `<span class="ai-ex-number">${n + 1}</span>`);
        await sleep(90);
      }
      break;
    }
    case "reveal":
      revealSentences(find(".ai-ex-sentences"));
      break;
    case "accept": {
      await sleep(500);
      const contact = document.createElement("span");
      contact.className = "ai-ex-contact";
      contact.innerHTML = '<span class="ai-ex-avatar">JN</span>Jan Novák';
      morphInto(find(".ai-ex-name"), contact);
      break;
    }
    case "dismiss":
      await sleep(700);
      find("[data-suggestion-dismiss]").click();
      break;
    case "move": {
      await sleep(500);
      const card = find(".ai-ex-move-card");
      relocate(card, () => card.classList.add("is-docked"));
      break;
    }
    case "warn":
      await settle(markerIn(find(".ai-ex-ring"), find(".ai-ex-marker-icon")));
      rippleOnce(find(".ai-ex-ripple"));
      break;
    case "work":
      break;
  }
}
function replayFromTile(event: MouseEvent, index: number) {
  if ((event.target as Element).closest("button")) return;
  void play(index);
}

onMounted(async () => {
  for (const index of tiles.keys()) {
    void play(index);
    await sleep(240);
  }
  for (const index of foundation.keys()) {
    void playFoundation(index);
    await sleep(240);
  }
});
</script>

<template>
  <SettingsSection title="Pohybový systém" description="Devět opakovaně použitých pohybů ze scény „Pohybový systém“ prototypu a pod nimi základ pro celé UI. Každou dlaždici hraje primitivum ze src/motion/aiMotion.ts nebo token a keyframes ze src/motion.css (styles/ai.css), tedy totéž, co používají komponenty. Klikněte na dlaždici a přehraje se znovu.">
    <div ref="root" class="ai-motion-example">
      <div class="ai-ex-tiles">
        <div v-for="(tile, index) in tiles" :key="tile.kind" class="ai-ex-tile" :data-tile="tile.kind">
          <div :ref="(el) => { minis[index] = (el as HTMLElement | null) ?? undefined; }" class="ai-ex-mini" @click="replayFromTile($event, index)">
            <div v-if="runs[index]" :key="runs[index]" class="ai-ex-stage">
              <div v-if="tile.kind === 'marker'" class="ai-ex-block">
                <span class="ai-ex-marker" aria-hidden="true"><svg class="ai-ex-ring" viewBox="0 0 26 26"><circle cx="13" cy="13" r="12" /></svg><span class="ai-ex-marker-icon"><AtlasIcon name="user" :size="13" /></span></span>
                Kontaktujte Jana Nováka…
              </div>
              <div v-else-if="tile.kind === 'solid'" class="ai-ex-card">
                <svg class="atlas-suggestion-outline" aria-hidden="true"><rect x=".5" y=".5" rx="11.5" ry="11.5" /></svg>
                <AiGlyph :size="14" /> Odpověď z wiki
              </div>
              <div v-else-if="tile.kind === 'gather'" class="ai-ex-gather">
                <div class="ai-ex-sources"><span class="ai-ex-pill ai-ex-source">VPN</span><span class="ai-ex-pill ai-ex-source">Certifikáty</span></div>
                <div class="ai-ex-answer"><span class="ai-ex-pill ai-ex-slot" style="visibility: hidden"><span class="ai-ex-number">1</span></span><span class="ai-ex-pill ai-ex-slot" style="visibility: hidden"><span class="ai-ex-number">2</span></span></div>
              </div>
              <div v-else-if="tile.kind === 'work'" class="ai-ex-working"><AiGlyph working /><span class="atlas-ai-shimmer">Připravuji odpověď…</span></div>
              <p v-else-if="tile.kind === 'reveal'" class="ai-ex-sentences"><span>FortiClient a firemní účet.</span> <span>Platný certifikát zařízení.</span> <span>Žádost přes Service Desk.</span></p>
              <p v-else-if="tile.kind === 'accept'" class="ai-ex-accept">Kontaktujte <span class="ai-ex-name">Jana Nováka</span>.</p>
              <SuggestionChip v-else-if="tile.kind === 'dismiss'" :apply-label="null">
                <template #glyph><AiGlyph :size="13" /></template>
                #vpn
              </SuggestionChip>
              <template v-else-if="tile.kind === 'move'"><div class="ai-ex-move-target" /><div class="ai-ex-move-card" /></template>
              <div v-else-if="tile.kind === 'warn'" class="ai-ex-block is-warning">
                <span class="ai-ex-marker is-warning" aria-hidden="true"><svg class="ai-ex-ring" viewBox="0 0 26 26"><circle cx="13" cy="13" r="12" /></svg><span class="ai-ex-marker-icon"><AtlasIcon name="triangle-alert" :size="13" /></span><span class="ai-ex-ripple" /></span>
                heslo <span class="ai-ex-secret">Srv#2026</span>
              </div>
            </div>
          </div>
          <div class="ai-ex-name-row">
            <strong>{{ tile.name }}</strong>
            <button type="button" class="ai-ex-replay" :aria-label="`Přehrát znovu: ${tile.name}`" @click="play(index)"><AtlasIcon name="rotate-cw" :size="13" /></button>
          </div>
          <span class="ai-ex-spec">{{ tile.spec }}</span>
          <span class="ai-ex-text">{{ tile.text }}</span>
          <code class="ai-ex-primitive">{{ tile.primitive }}</code>
        </div>
      </div>
      <ul class="ai-ex-rules">
        <li v-for="rule in rules" :key="rule"><AtlasIcon name="check" :size="15" />{{ rule }}</li>
      </ul>

      <h3 class="ai-ex-subtitle">Základ pro celé UI</h3>
      <p class="ai-ex-lead">Stejné tokeny a primitiva používá celá aplikace, nejen AI: hledání, nabídky, oznámení, načítání. Zpomalení 3× nesou tokeny samy (a primitiva jen své pevné délky z prototypu), takže se nic nezpomalí dvakrát. Omezený pohyb nechá prolínání do 160 ms a smyčky zastaví.</p>
      <div class="ai-ex-tiles">
        <div v-for="(tile, index) in foundation" :key="tile.kind" class="ai-ex-tile" :data-tile="tile.kind">
          <div :ref="(el) => { foundationMinis[index] = (el as HTMLElement | null) ?? undefined; }" class="ai-ex-mini" @click="replayFoundationFromTile($event, index)">
            <div v-if="foundationRuns[index]" :key="foundationRuns[index]" class="ai-ex-stage">
              <template v-if="tile.kind === 'search'">
                <span class="ai-ex-search-field"><AtlasIcon name="search" :size="12" />Hledat</span>
                <div class="ai-ex-search-panel" hidden>
                  <span class="ai-ex-search-query">vpn z domova</span>
                  <span v-for="row in ['Připojení k VPN', 'VPN pro dodavatele', 'Certifikáty zařízení']" :key="row" class="ai-ex-search-row">{{ row }}</span>
                </div>
              </template>
              <div v-else-if="tile.kind === 'page'" class="ai-ex-page-panel" data-search-view="dialog">
                <span class="ai-ex-search-query">vpn z domova<AtlasIcon name="maximize-2" :size="11" /></span>
                <span v-for="row in ['Připojení k VPN', 'VPN pro dodavatele', 'Certifikáty zařízení', 'Práce z domova']" :key="row" class="ai-ex-search-row">{{ row }}</span>
              </div>
              <template v-else-if="tile.kind === 'menu'">
                <span class="ai-ex-menu-trigger"><AtlasIcon name="more" :size="14" /></span>
                <!-- The same data-slot as a Reka menu, so motion.css plays exactly what the app's menus play. -->
                <div v-if="menuState" class="ai-ex-menu" data-slot="dropdown-menu-content" :data-state="menuState">
                  <span>Upravit</span><span>Přesunout</span><span>Odstranit</span>
                </div>
              </template>
              <div v-else-if="tile.kind === 'toast' && toastState" class="ai-ex-toast" :class="`is-${toastState}`" role="status"><AtlasIcon name="check" :size="13" />Zkopírováno</div>
              <div v-else-if="tile.kind === 'work'" class="ai-ex-work">
                <span class="ai-ex-work-row"><span class="atlas-spinner ai-ex-spinner" aria-hidden="true" />Kontroluji</span>
                <span class="ai-ex-work-row"><span class="atlas-pulse ai-ex-dot" aria-hidden="true" />Wiki · hledáme…</span>
                <LoadingSkeleton :rows="1" row-height="30px" label="Ukázka načítání" />
              </div>
              <div v-else-if="tile.kind === 'defer'" class="ai-ex-defer">
                <span>{{ deferState.label }}</span>
                <span class="ai-ex-defer-track"><span v-if="deferState.busy" class="ai-ex-defer-bar" /></span>
              </div>
              <div v-else-if="tile.kind === 'wave'" class="ai-ex-wave"><span v-for="row in 5" :key="row" class="ai-ex-line" /></div>
              <div v-else-if="tile.kind === 'loading'" class="ai-ex-wave ai-ex-loading"><span v-for="row in 5" :key="row" class="ai-ex-line" /></div>
              <div v-else-if="tile.kind === 'rise-from'" class="ai-ex-rise">
                <span class="ai-ex-rise-plain"><span v-for="row in 3" :key="row" class="ai-ex-line" /></span>
                <span class="ai-ex-rise-from"><span v-for="row in 3" :key="row" class="ai-ex-line" /></span>
              </div>
              <p v-else-if="tile.kind === 'pulse'" class="ai-ex-accept">Připojení přes <span class="ai-ex-match">VPN</span> z domova</p>
            </div>
          </div>
          <div class="ai-ex-name-row">
            <strong>{{ tile.name }}</strong>
            <button type="button" class="ai-ex-replay" :aria-label="`Přehrát znovu: ${tile.name}`" @click="playFoundation(index)"><AtlasIcon name="rotate-cw" :size="13" /></button>
          </div>
          <span class="ai-ex-spec">{{ tile.spec }}</span>
          <span class="ai-ex-text">{{ tile.text }}</span>
          <code class="ai-ex-primitive">{{ tile.primitive }}</code>
        </div>
      </div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.ai-ex-tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 12px; }
.ai-ex-tile { display: grid; align-content: start; gap: 8px; padding: 12px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); }
.ai-ex-mini { position: relative; display: grid; place-items: center; height: 92px; overflow: hidden; border-radius: 9px; background: var(--canvas); cursor: pointer; }
.ai-ex-stage { display: contents; }
.ai-ex-name-row { display: flex; align-items: center; justify-content: space-between; gap: 8px; font-size: 13px; }
.ai-ex-replay { display: grid; place-items: center; width: 24px; height: 24px; border: 0; border-radius: 6px; background: transparent; color: var(--muted); cursor: pointer; }
.ai-ex-replay:hover { background: var(--hover); color: var(--text); }
.ai-ex-spec, .ai-ex-primitive { font: 400 11.5px/1.5 ui-monospace, "Cascadia Mono", Consolas, monospace; color: var(--muted); }
.ai-ex-primitive { color: var(--accent); }
.ai-ex-text { color: var(--muted); font-size: 12.5px; }
.ai-ex-rules { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 8px 20px; margin: 16px 0 0; padding: 0; list-style: none; font-size: 13px; }
.ai-ex-rules li { display: flex; gap: 8px; }
.ai-ex-rules svg { flex: none; margin-top: 2px; color: var(--accent); }

/* Tile content: the smallest markup that shows each motion, sized like the prototype's tiles. */
.ai-ex-block { position: relative; width: 150px; padding-left: 30px; color: var(--muted); font-size: 12px; }
.ai-ex-block.is-warning { width: auto; padding-left: 34px; color: var(--text); }
.ai-ex-marker { position: absolute; top: 50%; left: 0; display: grid; place-items: center; width: 26px; height: 26px; margin-top: -13px; color: var(--muted); }
.ai-ex-marker.is-warning { color: var(--warning); }
.ai-ex-ring { position: absolute; inset: 0; width: 26px; height: 26px; overflow: visible; }
.ai-ex-ring circle { fill: var(--surface); stroke: var(--input-line); stroke-width: 1.2; stroke-dasharray: 3 2.4; }
.ai-ex-marker.is-warning .ai-ex-ring circle { fill: var(--warning-bg); stroke: var(--warning); stroke-dasharray: none; }
.ai-ex-marker-icon { position: relative; display: grid; place-items: center; }
.ai-ex-ripple { position: absolute; inset: 0; border: 2px solid var(--warning); border-radius: 50%; opacity: 0; }
.ai-ex-secret { padding: 0 3px; border-radius: 4px; background: var(--warning-bg); color: var(--warning); font-family: ui-monospace, "Cascadia Mono", Consolas, monospace; }
.ai-ex-card { position: relative; display: grid; place-items: center; align-content: center; width: 170px; height: 46px; border-radius: 12px; background: var(--surface); font-size: 12px; }
.ai-ex-card.is-solid { background: linear-gradient(180deg, color-mix(in srgb, var(--accent-soft) 45%, var(--surface)), var(--surface) 60%); }
.ai-ex-gather { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; align-items: center; width: 200px; }
.ai-ex-sources { display: grid; gap: 6px; justify-items: start; }
.ai-ex-answer { display: flex; flex-wrap: wrap; align-content: flex-start; gap: 6px; height: 60px; padding: 8px; border: 1px solid var(--line); border-radius: 12px; background: linear-gradient(180deg, color-mix(in srgb, var(--accent-soft) 45%, var(--surface)), var(--surface) 60%); }
.ai-ex-working { display: flex; align-items: center; gap: 8px; font-size: 13px; }
.ai-ex-sentences { width: 190px; margin: 0; font-size: 12px; line-height: 1.5; }
.ai-ex-sentences > span { display: inline-block; }
.ai-ex-accept { margin: 0; font-size: 12.5px; }
.ai-ex-move-target { position: absolute; top: 8px; right: 8px; bottom: 8px; width: 60px; border: 1px dashed var(--line); border-radius: 6px; }
.ai-ex-move-card { position: absolute; top: 22px; left: 12px; width: 90px; height: 46px; border: 1px solid var(--line); border-radius: 12px; background: linear-gradient(180deg, color-mix(in srgb, var(--accent-soft) 45%, var(--surface)), var(--surface) 60%); }
.ai-ex-move-card.is-docked { top: 8px; left: calc(100% - 68px); width: 60px; height: calc(100% - 16px); }
.ai-ex-subtitle { margin: 24px 0 4px; font-size: 15px; }
.ai-ex-lead { margin: 0 0 12px; color: var(--muted); font-size: 13px; }
.ai-ex-search-field { position: absolute; top: 10px; left: 50%; display: inline-flex; align-items: center; gap: 6px; width: 110px; margin-left: -55px; padding: 4px 8px; border: 1px solid var(--line); border-radius: 8px; background: var(--surface); color: var(--muted); font-size: 11.5px; }
.ai-ex-search-panel { position: absolute; inset: 6px; display: grid; align-content: start; gap: 2px; padding: 6px 8px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); box-shadow: 0 8px 24px rgb(0 0 0 / .08); font-size: 11.5px; }
.ai-ex-search-panel[hidden] { display: none; }
.ai-ex-search-query { padding-bottom: 3px; border-bottom: 1px solid var(--line); font-weight: 600; }
.ai-ex-search-row { padding: 1px 4px; border-radius: 5px; }
.ai-ex-page-panel { position: absolute; inset: 14px 34px; display: grid; align-content: start; gap: 2px; padding: 6px 8px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); box-shadow: 0 8px 24px rgb(0 0 0 / .08); font-size: 11.5px; }
.ai-ex-page-panel[data-search-view="page"] { inset: 0; border: 0; border-radius: 0; box-shadow: none; }
.ai-ex-page-panel .ai-ex-search-query { display: flex; justify-content: space-between; align-items: center; }
.ai-ex-search-row:first-of-type { background: var(--hover); }
.ai-ex-menu-trigger { position: absolute; top: 10px; left: 14px; display: grid; place-items: center; width: 24px; height: 24px; border: 1px solid var(--line); border-radius: 6px; background: var(--surface); }
.ai-ex-menu { position: absolute; top: 38px; left: 14px; display: grid; min-width: 110px; padding: 4px; border: 1px solid var(--line); border-radius: 8px; background: var(--surface); box-shadow: 0 8px 24px rgb(0 0 0 / .1); font-size: 11.5px; transform-origin: top left; }
.ai-ex-menu > span { padding: 2px 6px; border-radius: 4px; }
.ai-ex-menu > span:first-child { background: var(--hover); }
.ai-ex-toast { position: absolute; bottom: 10px; left: 50%; display: inline-flex; align-items: center; gap: 6px; margin-left: -56px; padding: 5px 10px; border: 1px solid var(--line); border-radius: 8px; background: var(--surface); box-shadow: 0 8px 24px rgb(0 0 0 / .1); color: var(--text); font-size: 12px; }
.ai-ex-toast svg { color: var(--success); }
.ai-ex-toast.is-in { animation: atlas-toast-in var(--atlas-motion-base) var(--atlas-ease-out); }
.ai-ex-toast.is-out { animation: atlas-toast-out var(--atlas-motion-surface) var(--atlas-ease-out) forwards; }
.ai-ex-work { display: grid; gap: 6px; width: 170px; font-size: 11.5px; }
.ai-ex-work :deep(.loading-skeleton-row) { padding: 3px 0; }
.ai-ex-work :deep(.loading-skeleton-icon) { flex-basis: 20px; height: 20px; }
.ai-ex-work :deep(.loading-skeleton-copy) { gap: 5px; }
.ai-ex-work-row { display: flex; align-items: center; gap: 8px; color: var(--muted); }
.ai-ex-spinner { width: 12px; height: 12px; border: 2px solid var(--line); border-top-color: var(--accent); border-radius: 50%; }
.ai-ex-dot { width: 7px; height: 7px; border-radius: 50%; background: var(--accent); }
.ai-ex-defer { display: grid; gap: 8px; width: 170px; font-size: 12px; }
.ai-ex-defer-track { position: relative; height: 2px; overflow: hidden; border-radius: 2px; background: var(--line); }
.ai-ex-defer-bar { position: absolute; inset: 0; background: var(--accent); animation: atlas-backdrop-in var(--atlas-motion-base) var(--atlas-ease-out) var(--atlas-motion-defer) both; }
.ai-ex-wave { display: grid; gap: 6px; width: 170px; }
.ai-ex-rise { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; width: 170px; }
.ai-ex-rise > span { display: grid; gap: 6px; }
.ai-ex-line { display: block; height: 8px; border-radius: 4px; background: color-mix(in srgb, var(--text) 14%, transparent); }
.ai-ex-line:nth-child(even) { width: 75%; }
.ai-ex-match { background: color-mix(in srgb, var(--warning) 30%, transparent); }
/* The editor's loading wave on its keyframes and tokens (DocumentEditor.vue draws it over the page; here over the tile). */
.ai-ex-loading { position: absolute; inset: 0; place-content: center; justify-items: center; width: auto; overflow: clip; }
.ai-ex-loading .ai-ex-line { width: 170px; }
.ai-ex-loading::before { content: ""; position: absolute; inset: 0 0 auto; height: 40vh; background: linear-gradient(to bottom, transparent, color-mix(in srgb, var(--accent) 12%, transparent), transparent);
  transform: translateY(-40vh); animation: atlas-loading-wave var(--atlas-motion-loop) linear var(--atlas-motion-defer) infinite; }
@media (prefers-reduced-motion: reduce) { .ai-ex-loading::before { inset: 0; height: auto; background: color-mix(in srgb, var(--accent) 4%, transparent); transform: none; animation: none; } }
:global([data-atlas-reduced-motion]) .ai-ex-loading::before { inset: 0; height: auto; background: color-mix(in srgb, var(--accent) 4%, transparent); transform: none; animation: none; }
</style>

<style>
/* Shared with copies that aiMotion places outside this component (flyGhost, morphInto). */
.ai-ex-pill { display: inline-flex; align-items: center; gap: 6px; padding: 3px 8px; border: 1px solid var(--line); border-radius: 7px; background: var(--surface); font-size: 12px; white-space: nowrap; }
.ai-ex-number { display: inline-block; padding: 2px 4px; border-radius: 4px; background: var(--accent-soft); color: var(--accent); font: 600 10.5px/1 ui-monospace, "Cascadia Mono", Consolas, monospace; }
.ai-ex-contact { display: inline-flex; align-items: center; gap: 5px; overflow: hidden; padding: 1px 8px 1px 2px; border: 1px solid var(--line); border-radius: 99px; background: var(--hover); font-weight: 600; line-height: 20px; white-space: nowrap; vertical-align: -4px; }
.ai-ex-avatar { display: grid; place-items: center; flex: none; width: 20px; height: 20px; border-radius: 50%; background: var(--space-coral-bg, #ffd9c7); color: var(--space-coral-fg, #8a3a12); font-size: 8.5px; font-weight: 700; }
</style>
