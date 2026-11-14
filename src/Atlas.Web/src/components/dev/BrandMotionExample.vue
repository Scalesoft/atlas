<script setup lang="ts">
import { ref } from "vue";
import AtlasBrandLogo from "../AtlasBrandLogo.vue";
import AtlasMotionMark from "../brand/AtlasMotionMark.vue";
import LoginLayout from "../auth/LoginLayout.vue";
import SetupWizard from "../auth/SetupWizard.vue";
import FormField from "../FormField.vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";

const replay = ref(0);
const playing = ref(true);
const secondFactor = ref(false);
const setupStep = ref(1);
const setupDone = ref(false);
const setupForm = ref({ workspaceName: 'Týmová wiki', displayName: '', username: '', password: '', bootstrapToken: '' });
const motifs = [
  { variant: "settings", title: "Ladění", description: "Jednorázová reakce při otevření nastavení." },
  { variant: "bookmark", title: "Záložka", description: "Potvrzení až po úspěšném uložení." },
  { variant: "network", title: "Souvislosti", description: "Smyčka pro hledání souvislostí mezi zdroji." },
  { variant: "shelf", title: "Knihovna", description: "Jednorázové přivítání v přehledu prostorů." },
  { variant: "pages", title: "Listování", description: "Smyčka při procházení dokumentů." },
  { variant: "scan", title: "Čtení", description: "Smyčka při hledání relevantní pasáže." },
  { variant: "gather", title: "Syntéza", description: "Smyčka při sestavování odpovědi." },
] as const;
</script>

<template>
  <SettingsPage title="Značka a pohyb" description="Schválené animace Atlasu. Živé komponenty, místní data, žádné přihlášení ani požadavky na server.">
    <SettingsSection title="AtlasBrandLogo" description="Značka aplikace: ambient pro malé logo, hero pro úvodní rozevření, static vedle dalších animací. AtlasIcon dál slouží pro běžné ovládací prvky.">
      <div class="st-actions"><Button @click="replay++">Přehrát od začátku</Button><Button variant="outline" @click="playing = !playing">{{ playing ? 'Pozastavit' : 'Pokračovat' }}</Button></div>
      <div class="brand-examples" :class="{ paused: !playing }">
        <div v-for="motion in (['ambient', 'hero', 'static'] as const)" :key="motion">
          <div class="brand-large"><AtlasBrandLogo :key="`${motion}-${replay}`" :motion="motion" /></div>
          <div class="brand-caption"><span class="brand-small"><AtlasBrandLogo :key="replay" :motion="motion" /></span>{{ motion }}</div>
        </div>
      </div>
    </SettingsSection>
    <SettingsSection title="LoginLayout" description="Obal přihlášení se slotem pro existující formulář. Dekorace zůstává namontovaná při změně kroku. Ukázka 2FA je pouze návrh; aplikace používá skutečně dostupné metody ze serveru.">
      <div :class="{ paused: !playing }">
        <LoginLayout :key="replay" wiki-name="Atlas">
          <section class="entry-card">
            <div class="entry-brand"><span class="brand-mark"><AtlasBrandLogo motion="static" /></span>Atlas</div>
            <template v-if="!secondFactor">
              <h2>Vítejte zpět.</h2><p>Váš prostor pro znalosti.</p>
              <div class="demo-providers"><Button v-for="provider in ['Pracovní účet', 'Google', 'GitHub']" :key="provider" variant="outline" @click="secondFactor = true">Pokračovat přes {{ provider }}</Button></div>
              <FormField label="Uživatelské jméno"><Input placeholder="alex" disabled /></FormField>
              <FormField label="Heslo"><Input type="password" placeholder="Ukázkové heslo" disabled /></FormField>
              <Button class="demo-continue" @click="secondFactor = true">Další krok (ukázka)</Button>
            </template>
            <template v-else>
              <h2>Ověřte, že jste to vy.</h2><p>Náhled budoucího druhého faktoru.</p>
              <FormField label="Ověřovací kód"><Input model-value="123 456" readonly /></FormField>
              <Button variant="outline" @click="secondFactor = false">Zpět k přihlášení</Button>
            </template>
          </section>
        </LoginLayout>
      </div>
    </SettingsSection>
    <SettingsSection title="SetupWizard" description="První instalace: název wiki a správce. Rozevření loga přivítá uživatele, krátký příchod formuláře označí nový krok. Technická nápověda se rozbalí až na vyžádání; omezený pohyb vypne animace.">
      <div class="st-actions"><Button variant="outline" @click="setupStep = 1; setupDone = false">První krok</Button><Button variant="outline" @click="setupStep = 2; setupDone = false">Správce a token</Button></div>
      <p v-if="setupDone" role="status">Ukázka dokončena. Žádné údaje se neodeslaly.</p>
      <SetupWizard v-model:step="setupStep" :form="setupForm" :status="{ requiresSetup: true, databaseReady: true, bootstrapTokenRequired: true, passwordMinimumLength: 12 }" :pending="false" error="" @complete="setupDone = true" />
    </SettingsSection>
    <SettingsSection title="AtlasMotionMark" description="Animované ilustrace činností, nikoli ikony tlačítek ani náhrada LoadingSkeleton. Zatím připravené k pozdějšímu použití. active zapíná pohyb, sousední text vždy popisuje skutečný stav.">
      <div class="motif-examples" :class="{ paused: !playing }">
        <div v-for="motif in motifs" :key="motif.variant" class="motif-example">
          <div class="motif-large"><AtlasMotionMark :key="replay" :variant="motif.variant" active /></div>
          <div class="brand-caption"><span class="brand-small"><AtlasMotionMark :key="replay" :variant="motif.variant" active /></span><strong>{{ motif.title }}</strong></div>
          <p>{{ motif.description }}</p>
        </div>
      </div>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.brand-examples, .motif-examples { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(200px, 100%), 1fr)); gap: 24px; margin-top: 24px; }
.brand-large { width: 120px; height: 120px; margin: 0 auto 16px; }
.brand-small { display: inline-flex; width: 32px; height: 32px; flex: none; }
.brand-caption { display: flex; align-items: center; justify-content: center; gap: 10px; }
.motif-large { width: 100px; height: 90px; margin: 0 auto 16px; }
.motif-example p { text-align: center; font-size: 12px; color: var(--muted); }
.demo-providers { display: grid; gap: 10px; margin: 24px 0; }
.demo-continue { width: 100%; margin-top: 20px; }
.paused :deep(*) { animation-play-state: paused !important; }
</style>
