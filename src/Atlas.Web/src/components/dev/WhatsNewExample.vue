<script setup lang="ts">
import { ref } from "vue";
import { compareReleases, parseReleaseNote, releaseNotes } from "../../whatsNew/releaseNotes";
import { showWhatsNew } from "../../whatsNew/notice";
import WhatsNewPage from "../WhatsNewPage.vue";
import ReleaseEntry from "../release/ReleaseEntry.vue";
import ReleaseMark from "../release/ReleaseMark.vue";
import { Button } from "../ui/button";

// „Co je nového“: the release components, driven only by the notes in `release-notes/` (whatsNew/releaseNotes.ts). Every minor or
// major release brings a codename, a mark (tooling/brand) and `### Hlavně`; nothing of a release is built into the components, as
// the made-up Atlas 4.2 „Witty Watermelon“ below shows (its mark is Bloom's, standing in for one made by the generator).
// The notice is the one notification system (a toast at the top, P6); here it records nothing, „seen“ only writes the line below.
// The changelog from 3.4 on: whole named versions with their patches.
const recent = releaseNotes.filter(note => compareReleases(note.version, "3.4.0") <= 0);
const pear = releaseNotes.find(note => note.version === "3.5.0") ?? releaseNotes[0]!;
const future = (mark: string) => parseReleaseNote("4.2.0", `codename: Witty Watermelon\n${mark ? `mark: ${mark}\n` : ""}\n*9. 3. 2027*\n\n### Hlavně\n`
  + "Tabule a kontakty v jednom pohledu.\n- **Tabule** `Alt T`: nové rozvržení sloupců.\n- **Kontakty:** čipy se načtou hned.\n\n"
  + "### Nové\n- **Tabule:** nové rozvržení sloupců.\n- **Kontakty:** čipy se načtou hned.\n");
const watermelon = future("bloom"), unmarked = future("");
const futureHotfix = parseReleaseNote("4.2.1", "*16. 3. 2027*\n\n### Opravy\n- Tabule si pamatuje sbalené sloupce.\n");
const futureHotfix2 = parseReleaseNote("4.2.2", "*23. 3. 2027*\n\n### Opravy\n- **Kontakty:** fotka se po nahrání ukáže hned.\n"
  + "- **Tabule:** přesun karty klávesnicí drží fokus.\n- **Hledání:** první výsledek je vybraný i po filtru.\n- **Export PDF** zachová barvy štítků.\n");
const futurePatches = [futureHotfix2, futureHotfix];
// The renderer takes its theme from the host (App passes it); the gallery reads the mode it was opened in.
const dark = document.documentElement.dataset.mode === "dark";

const run = ref(0);
const covers = ref(0);
const log = ref("");
const changelog = ref<HTMLElement>();
function announce(note = pear, release = note.version, since?: string) {
  log.value = "";
  showWhatsNew(release, {
    open: () => changelog.value?.scrollIntoView({ behavior: "smooth", block: "start" }),
    seen: () => { log.value = `Verze ${release} je viděná (v aplikaci se uloží na serveru).`; },
  }, note, since);
}
</script>

<template>
  <div class="whats-new-example">
    <h3>Po nové verzi: WhatsNewPage se splashem (ReleaseSplash)</h3>
    <p class="whats-new-example-note">
      Uvítání jednou po aktualizaci: otevře se z hlášky, nebo když uživatel stránku otevře poprvé po nové verzi. Ukáže motiv nejnovější
      pojmenované verze a jen to, co je nového od jeho naposledy viděné verze (tady 3.4.0): větu a karty z oddílu Hlavně, ostatní
      vydání v jednom pruhu. Značka se rozvine, texty vyjedou vlnou po ní. Klik nebo klávesa intro přeskočí, Enter pokračuje do
      Atlasu, Esc zavře, Celý changelog přepne na historii. Pro návrat kdykoli slouží ReleaseChangelog, ne splash.
    </p>
    <p><Button type="button" variant="outline" data-whats-new-replay @click="run++">Přehrát znovu</Button></p>
    <WhatsNewPage :key="run" current="3.5.0" since="3.4.0" :dark="dark" @close="log = 'Pokračovat do Atlasu (v aplikaci domovská stránka).'" />
    <p class="whats-new-example-log" role="status">{{ log }}</p>

    <h3>Zakrytá značka: ReleaseCover (4.0 Quiet Quince)</h3>
    <p class="whats-new-example-note">
      Jen pro verzi se zásadní změnou (<code>cover: secret</code> v jejím popisu), jinak ReleaseMark. Značka leží pod neviditelným
      inkoustem z editoru (skrytý obsah v režimu setření, @atlas/markdown/concealment) a uživatel ho setře tahem myši nebo prstu jako
      los; po 55 % se zbytek rozestoupí sám a kdoule pod ním se vykreslí a zaleskne. Klávesnicí Tab na krytí a Enter nebo mezerník
      odkryje hned; se sníženým pohybem bez kruhu, krátkým prolnutím. Krytí je jen na uvítání po aktualizaci (poznámky verze ještě neviděné); přehraná z changelogu je značka odkrytá.
    </p>
    <p><Button type="button" variant="outline" data-whats-new-cover-again @click="covers++">Zakrýt znovu</Button></p>
    <WhatsNewPage :key="`c${covers}`" current="4.0.0" since="3.5.2" :dark="dark" @close="log = 'Pokračovat do Atlasu.'" />

    <h3>Živé ukázky v kartách: Atlas 5.1 Tandem Tangerine</h3>
    <p class="whats-new-example-note">
      Řádek <code>demos: pointers agent qr graph</code> v popisu verze dá zvýrazněním z oddílu Hlavně postupně živou ukázku
      (components/release/demos, každá vlastní líný chunk, jen ukázková data); <code>-</code> zvýraznění ukázku nedá. Karty s ukázkou
      nevyjedou vlnou, ale vyletí z motivu a první z nich je dvakrát širší. Mandarinka se rozkrojí a dva ukazatele spolupracovníků
      (Jana a Anna) ji spolu roztáhnou. Ukázky: ukazatele, které kloužou na stejné pružině jako na živé stránce, a píšou; kurzor AI;
      karta osoby a její QR kód, který vyroste po kostičkách; malá mapa znalostí s otázkou „Kdo umí…“. Se sníženým pohybem a pod
      <code>[data-still]</code> stojí každá jako hotový obrázek. Esc splash zavře kdykoli, i během intra.
    </p>
    <WhatsNewPage :key="`t${run}`" current="5.1.0" since="5.0.0" :dark="dark" @close="log = 'Pokračovat do Atlasu.'" />

    <h3>Vymyšlená budoucí verze: Atlas 4.2 Witty Watermelon</h3>
    <p class="whats-new-example-note">Stejné komponenty nad jiným popisem verze: jiné kódové jméno, značka, datum, zvýraznění i dvě opravné verze.</p>
    <WhatsNewPage :key="`q${run}`" :notes="[...futurePatches, watermelon]" current="4.2.2" since="4.1.0" :dark="dark" @close="log = 'Pokračovat do Atlasu.'" />

    <h3>Po opravné verzi: malá aktualizace známé verze</h3>
    <p class="whats-new-example-note">
      Účet zná 4.2.0 a server běží na 4.2.2: nejde o novou pojmenovanou verzi, ale o drobné opravy Witty Watermelon. Splash je menší,
      v nadpisu zůstává pojmenovaná verze, nad ním opravná verze s datem a místo karet Hlavně je seznam oprav. Hláška zní
      „Atlas 4.2.2 — opravy verze 4.2 Witty Watermelon“.
    </p>
    <WhatsNewPage :key="`p${run}`" :notes="[...futurePatches, watermelon]" current="4.2.2" since="4.2.0" :dark="dark" @close="log = 'Pokračovat do Atlasu.'" />

    <h3>ReleaseEntry: záznam verze v changelogu</h3>
    <p class="whats-new-example-note">
      Pojmenovaná verze (major.minor) s kódovým jménem a značkou má značku prolnutou do ztracena za nadpisem a jemnou záři; bez značky
      (nebo bez kódového jména) vypadá jako dřív. Její opravné verze (patch) stojí pod ní, menší, na tenké lince: řádek s verzí a datem,
      nejnovější nahoře; delší než tři body je sbalená s náhledem prvního bodu (Enter nebo mezerník ji rozbalí). Opravná verze nikdy
      nemá vlastní záznam vedle pojmenované. Používá ho jen ReleaseChangelog.
    </p>
    <div class="whats-new-example-entries settings-ui">
      <ReleaseEntry :note="watermelon" :patches="futurePatches" current="4.2.2" :dark="dark" />
      <ReleaseEntry :note="unmarked" :dark="dark" />
    </div>

    <h3>ReleaseMark: značka verze</h3>
    <p class="whats-new-example-note">
      Dekorace vedle textu, který verzi jmenuje (alt="" a aria-hidden). Velká s introm na splashi, vodoznak v changelogu, 20 px v hlášce.
      Animace je uvnitř SVG a s prefers-reduced-motion ukáže rovnou konečný stav. Na logo aplikace patří AtlasBrandLogo.
    </p>
    <div class="whats-new-example-marks">
      <ReleaseMark v-if="pear.mark" :key="`m${run}`" :mark="pear.mark" variant="hero" />
      <div class="whats-new-example-watermark"><ReleaseMark v-if="pear.mark" :mark="pear.mark" variant="watermark" /></div>
      <ReleaseMark v-if="pear.mark" :mark="pear.mark" variant="icon" />
      <ReleaseMark :key="`q${run}`" mark="quince" variant="hero" />
      <ReleaseMark :key="`r${run}`" mark="raspberry" variant="hero" />
      <ReleaseMark :key="`s${run}`" mark="strawberry" variant="hero" />
      <ReleaseMark mark="strawberry" variant="icon" />
    </div>
    <p class="whats-new-example-note">Kdoule (4.0 Quiet Quince) je z teček místo drátů a má tichý závěr: s třemi pomalými odlesky tečky postupně zhasínají, zbude řídká stopa a nic se nehoupe. Malina (4.1 Radiant Raspberry) je shluk kuliček se zářivým závěrem: záře od středu sílí, kuličky se rozsvítí a celá malina se rozplyne ve světle; ve vodoznaku a ikoně zůstává celá. Jahoda (Shared Strawberry, vydání s otevřeným kódem) je narýsovaná podle zlatého řezu: ve zlatém obdélníku vyroste jahoda, nažky naskakují po zlatém úhlu 137,5°, po zlaté spirále z ní vyrazí šlahoun a na něm menší jahůdky (forky), každá 1/φ té předchozí; kóty a pomocné čáry nakonec zmizí.</p>

    <h3>Hláška o nové verzi</h3>
    <p class="whats-new-example-note">
      Po přihlášení s novou verzí se nahoře ukáže hláška s kódovým jménem a malou značkou; Zobrazit otevře stránku Co je nového, × nebo
      Esc (s klávesnicí v hlášce, Alt+T) ji zavře. Obojí verzi uloží jako viděnou, jednou na účet. Nový účet a vývojová verze (-dev)
      hlášku nedostanou.
    </p>
    <p class="whats-new-example-actions">
      <Button type="button" variant="outline" data-whats-new-announce @click="announce()">Ukázat hlášku o verzi 3.5</Button>
      <Button type="button" variant="outline" @click="announce(watermelon)">Hláška vymyšlené verze 4.2</Button>
      <Button type="button" variant="outline" @click="announce(watermelon, '4.2.2', '4.2.0')">Hláška opravné verze 4.2.2</Button>
    </p>

    <h3>ReleaseChangelog: celá historie</h3>
    <p class="whats-new-example-note">
      Stránka Co je nového kdykoli (paleta, Profil, klik na verzi ve Správě wiki). Klik na kódové jméno větší verze přehraje její splash
      i s animací, bez řádku „od vaší poslední verze“; Celý changelog vrátí historii.
    </p>
    <div ref="changelog"><WhatsNewPage :notes="recent" current="3.5.2" :dark="dark" @close="log = 'Pokračovat do Atlasu.'" /></div>
  </div>
</template>

<style scoped>
.whats-new-example h3 { margin: 28px 0 6px; font-size: 16px; }
.whats-new-example-note { max-width: 70ch; color: var(--muted); font-size: 13px; line-height: 1.5; }
.whats-new-example-log { min-height: 1.5em; color: var(--muted); font-size: 13px; }
.whats-new-example-actions { display: flex; flex-wrap: wrap; gap: 8px; }
.whats-new-example-entries { display: flex; flex-direction: column; gap: 12px; max-width: 760px; }
.whats-new-example-marks { display: flex; flex-wrap: wrap; align-items: center; gap: 24px; }
.whats-new-example-marks > .release-mark[data-variant="hero"] { width: 160px; }
.whats-new-example-watermark { position: relative; overflow: clip; width: 240px; height: 140px; border: 1px solid var(--line); border-radius: var(--st-radius, 14px); background: var(--surface); }
.whats-new-example-watermark > .release-mark { position: absolute; right: -28px; top: -32px; }
</style>
