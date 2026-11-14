<script setup lang="ts">
import { onBeforeUnmount, ref } from "vue";
import type { SpaceCover, SpaceCoverChoice } from "../../../spaceCover";
import AppearancePicker from "../../settings/AppearancePicker.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import SpaceCoverField from "../../library/SpaceCoverField.vue";
import SpaceCoverGallery from "../../library/SpaceCoverGallery.vue";
import { TooltipProvider } from "../../ui/tooltip";
import { galleryDrawing } from "./libraryGalleryData";

// Gallery only: the image of the space goes through a local stand-in of spaceCoverApi, so choosing a drawing,
// uploading, dropping and blending all work without a server; the chosen file stays in the browser.
const icon = ref("server");
const color = ref("teal-2");
/** What the stand-in server "picks" for this space. */
const automatic = "it-monitoring";
const cover = ref<SpaceCover | null>({ source: "Auto", catalogKey: automatic, version: null, tinted: true });
/** The gallery shown on its own below the block: it only reports a choice, its owner keeps it. */
const picked = ref<Pick<SpaceCover, "source" | "catalogKey">>({ source: "Catalog", catalogKey: "it-backup" });
const image = ref(galleryDrawing("servers"));
let version = 1;
const wait = () => new Promise((resolve) => setTimeout(resolve, 600));
const api = {
  async upload(_spaceId: string, file: File) {
    await wait();
    if (image.value.startsWith("blob:")) URL.revokeObjectURL(image.value);
    image.value = URL.createObjectURL(file);
    return { source: "Upload" as const, catalogKey: null, version: String(++version), tinted: cover.value?.tinted ?? true };
  },
  async setTinted(_spaceId: string, tinted: boolean) { await wait(); return { source: "Upload" as const, catalogKey: null, version: String(version), tinted }; },
  async choose(_spaceId: string, choice: SpaceCoverChoice): Promise<SpaceCover> {
    await wait();
    return { source: choice.source, catalogKey: choice.source === "Auto" ? automatic : choice.catalogKey ?? null, version: null, tinted: true };
  },
  async match() { return { catalogKey: automatic }; },
};
onBeforeUnmount(() => { if (image.value.startsWith("blob:")) URL.revokeObjectURL(image.value); });
</script>

<template>
  <SettingsSection
    title="Vzhled prostoru: AppearancePicker a SpaceCoverField"
    icon="palette"
    description="settings/AppearancePicker.vue nabízí nejdřív běžné ikony a 10 rychlých barev (deska 11); celá sada ikon s hledáním a kategoriemi se otevře pod Další ikony a paleta 12 barev × 5 tónů pod Další barvy a tóny. Volba mimo rychlé zůstane vidět: současná ikona se přidá mezi běžné, uložená barva mimo deset rovnou otevře paletu. title udělá z náhledu hlavu sekce, varianta tile ukazuje i řádek v postranním panelu. library/SpaceCoverField.vue je blok Obrázek prostoru vedle živé karty: řádek říká, jak byl obrázek vybrán (automaticky podle názvu, popisu a ikony, z katalogu, vlastní, nebo žádný), Vybrat jiný otevře galerii vestavěných kreseb, Nahrát vlastní přijme PNG, JPEG nebo WebP do 5 MB (tlačítko nebo přetažení) a u nahraného obrázku je Sladit do barvy prostoru; ukládá se hned samo, mimo lištu Uložit, volbu ukáže hned a při odmítnutí vrátí předchozí obrázek, chyby ukazuje na místě a server bez obrázků vysvětlí. Pod blokem je samostatně otevřená library/SpaceCoverGallery.vue, výběr obrázku pohledem: Automaticky, Bez obrázku a všechny kresby v barvě prostoru, s hledáním v názvech, štítcích a kmenech slov z katalogu a se čtyřmi skupinami; po napsání je první kresba předvybraná, Enter ji vezme jako klik (nahraný obrázek tím nahradí), Tab ji vezme jako šipka (nahraný obrázek zůstane, dokud ho nepotvrdí Enter), první Esc hledání vymaže. Je to jedna skupina přepínačů (šipky procházejí a rovnou vybírají), proto u každé volby hlásí, zda vznikla klávesnicí, a vlastník ji uloží, až když se výběr zastaví. Nad nahraným obrázkem blok volbu klávesnicí jen ukáže a uloží ji až po klávese Enter na té volbě, protože uložením se nahraný soubor smaže. Volte ji místo SearchableChoiceList, když se možnosti poznají obrázkem a ne řádkem textu; ikony a barvy z katalogu vzhledu patří do AppearancePicker."
  >
    <TooltipProvider>
      <div class="settings-ui library-appearance-example">
        <AppearancePicker v-model:icon="icon" v-model:color="color" title="Vzhled" name="Runbooky a provoz" icons-label="Ikona prostoru" />
        <SpaceCoverField
          :space="{ id: 'gallery', name: 'Runbooky a provoz', icon, color, role: 'Admin', pageCount: 110, lastChangedAt: null }"
          :cover="cover"
          :match-input="{ name: 'Runbooky a provoz', description: '', icon }"
          :cover-src="image"
          :api="api"
          @change="cover = $event"
        />
        <SpaceCoverGallery :space="{ id: 'gallery', icon, color }" :cover="picked" :auto-key="automatic" @choose="picked = { source: $event.source, catalogKey: $event.catalogKey ?? null }" />
      </div>
    </TooltipProvider>
  </SettingsSection>
</template>

<style scoped>
.library-appearance-example { display: grid; gap: 20px; min-width: 0; }
</style>
