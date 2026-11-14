<script setup lang="ts">
import { ref } from "vue";
import { ApiError } from "../../../api";
import ChangeSummaryField from "../../publishing/ChangeSummaryField.vue";
import PublishControl from "../../publishing/PublishControl.vue";
import { changeSummaryError, normalizeChangeSummary, type PublicationCheck } from "../../../publishing/publication";
import { Button } from "../../ui/button";
import AtlasIcon from "../../AtlasIcon.vue";
import PagePublicationState from "../../publishing/PagePublicationState.vue";
import { draftWording } from "../../../publishing/pagePublication";
import EditLockToggle from "../../EditLockToggle.vue";
import SettingsPage from "../../settings/SettingsPage.vue";
import SettingsSection from "../../settings/SettingsSection.vue";

/** Local data only: "publishing" waits a moment and, for a summary containing "chyba", fails like the server's 400. */
const status = ref("Koncept");
const live = ref<InstanceType<typeof PublishControl>>();
async function publish(summary: string | null) {
  status.value = "Publikuji…";
  await new Promise(resolve => setTimeout(resolve, 500));
  if (summary?.toLowerCase().includes("chyba")) { status.value = "Koncept"; throw new ApiError(400, changeSummaryError()); }
  status.value = "Publikováno";
  return true;
}
const warning: PublicationCheck[] = [{ id: "secret", severity: "warning", text: "Možné heslo v textu" }];
// A week ago, so the date reads the same way as in the application („31. 10.“).
const publishedAt = new Date(Date.now() - 7 * 86_400_000).toISOString();
const viewingPublished = ref(false);
const typed = ref("Doplněn postup pro mobilní zařízení.");
const tooLong = ref("Velmi dlouhý popis. ".repeat(30));
</script>

<template>
  <SettingsPage title="Publikace" description="Rozdělené tlačítko Publikovat a pole Popis změny. Živé komponenty, místní data, žádné požadavky na server.">
    <SettingsSection
      title="PublishControl"
      description="Hlavní část publikuje hned jako dnes, šipka otevře ukotvený nemodální panel „Před publikací“ s polem Popis změny. Zvolte ho místo ConfirmDialog nebo ActionDisclosure, když má volba patřit k jedné akci u jejího tlačítka a nesmí blokovat stránku; nález kontroly před publikací se do panelu vkládá slotem findings. Bez ikony AI: pole je pravidlová volba autora."
    >
      <div class="publish-example-row">
        <PublishControl ref="live" :publish="publish" can-discard @discard="status = 'Zahodit změny… (hostitel ukáže porovnání s publikovanou verzí a potvrzení)'" />
        <Button variant="outline" type="button" @click="live?.open()">Otevřít panel</Button>
        <span class="publish-example-status" role="status">{{ status }}</span>
      </div>
      <p class="publish-example-note">Zkuste popis s textem „chyba“: panel zůstane otevřený, popis se zachová a zobrazí se odpověď serveru. Escape = Zpět k úpravám, Enter v poli = Publikovat. Šipku zkuste přepnout rychle i během zavírání: panel naváže na právě zobrazený stav a rozepsaný popis zachová. Během publikace jsou obě části tlačítka nedostupné. S can-discard (stránka má publikovanou verzi) panel vlevo nabízí „Zahodit změny…“: panel se zavře a hostitel v ConfirmDialog (size wide) ukáže HistoryChanges konceptu proti publikované verzi; samotné zahození je obnova publikované verze z historie.</p>
    </SettingsSection>
    <SettingsSection
      title="Koncept v horní liště (PagePublicationState)"
      description="Kdo smí stránku upravovat, píše rovnou do ní a stránka se ukládá sama (SaveState). Čtenáři, hledání i asistent vidí jen publikovanou verzi, a horní lišta to pisateli říká: koncept lištu jemně tónuje do barvy zvýraznění (data-publication=&quot;draft&quot;, refresh-theme.css) a štítek uvádí, kterou verzi čtenáři vidí; tlačítko „Zveřejnit změny“ jednou větou v nápovědě řekne, co udělá. Stav u názvu stránky koncept neopakuje. Přepínač „Koncept | Publikovaná“ ukáže publikovanou verzi jen pro čtení (Historie, HistoryRevisionPreview), lišta pak tónování ztratí a „Zpět ke konceptu“ nebo Escape vrátí koncept; živý editor mezitím běží skrytý. Klidná lišta bez tlačítka znamená, že čtenáři vidí totéž; Živě znamená, že vidí změny hned. Čtenář lištu se stavem nevidí. PageStatusBadge zůstává pro seznamy stránek. Na telefonu (do 680 px) zůstane jen „Koncept“, celá věta je v nápovědě a pro čtečku."
    >
      <div class="publish-example-bars">
        <div><small>Publikováno</small><div class="publication-bar publish-example-bar" data-publication="published"><EditLockToggle :locked="false" shortcut="E" /></div></div>
        <div><small>Koncept (Ručně) · zkuste přepnout na Publikovaná</small><div class="publication-bar publish-example-bar" :data-publication="viewingPublished ? 'published' : 'draft'">
          <PagePublicationState v-model:viewing-published="viewingPublished" state="draft" published :published-at="publishedAt" can-view-published /><EditLockToggle :locked="false" shortcut="E" />
          <PublishControl v-if="!viewingPublished" label="Zveřejnit změny" :description="draftWording(true, publishedAt).publishHint" :publish="publish" />
        </div></div>
        <div><small>Koncept (Automaticky)</small><div class="publication-bar publish-example-bar" data-publication="draft">
          <PagePublicationState state="draft" published :published-at="publishedAt" automatic /><EditLockToggle :locked="false" shortcut="E" />
          <PublishControl label="Zveřejnit změny" :description="draftWording(true, publishedAt).publishHint" :publish="publish" />
        </div></div>
        <div><small>Koncept, zatím nepublikováno</small><div class="publication-bar publish-example-bar" data-publication="draft">
          <PagePublicationState state="draft" :published="false" /><EditLockToggle :locked="false" shortcut="E" />
          <PublishControl label="Zveřejnit změny" :description="draftWording(false).publishHint" :publish="publish" />
        </div></div>
        <div><small>Živě</small><div class="publication-bar publish-example-bar" data-publication="live"><PagePublicationState state="live" published /><EditLockToggle :locked="false" shortcut="E" /></div></div>
        <div><small>Zamčená stránka (Jen pro čtení + Upravit)</small><div class="publication-bar publish-example-bar" data-publication="published" data-example-locked><EditLockToggle :locked="true" shortcut="E" /></div></div>
        <div><small>Zamčený koncept: Upravit je jediné primární tlačítko, Zveřejnit změny je tiché</small><div class="publication-bar publish-example-bar" data-publication="draft" data-example-locked-draft>
          <PagePublicationState state="draft" published :published-at="publishedAt" can-view-published /><EditLockToggle :locked="true" shortcut="E" />
          <PublishControl label="Zveřejnit změny" quiet :description="draftWording(true, publishedAt).publishHint" :publish="publish" />
        </div></div>
        <div><small>Publikovaná verze (Zpět ke konceptu)</small><div class="publication-bar publish-example-bar" data-publication="published" data-example-published-view>
          <PagePublicationState :viewing-published="true" state="draft" published :published-at="publishedAt" can-view-published /><EditLockToggle :locked="false" shortcut="E" />
        </div></div>
      </div>
    </SettingsSection>
    <SettingsSection title="Zavřené, čekání, varování" description="Tři stavy samotného tlačítka. Při varování z kontroly zní potvrzení „Publikovat i tak“ (balík E).">
      <div class="publish-example-row">
        <div><small>Zavřené</small><div><PublishControl :publish="publish" /></div></div>
        <div><small>Čekání (pending)</small><div><PublishControl :publish="publish" pending /></div></div>
        <div><small>Se zákazem</small><div><PublishControl :publish="publish" disabled /></div></div>
        <div><small>S varováním</small><div><PublishControl :publish="publish" :checks="warning" /></div></div>
      </div>
    </SettingsSection>
    <SettingsSection
      title="SpaceDraftApproval: koncept, který se právě píše"
      description="Schválení konceptů (Prostor › Koncepty, jen správce wiki) schvaluje vždy přesnou revizi, kterou správce viděl. Stránka se ukládá sama, takže koncept se může během schvalování posunout: server odpoví 409 a řádek se načte znovu s tlačítkem „Schválit novou verzi“. To otevře náhled s porovnáním proti dříve viděné revizi a teprve z něj se schválí nová verze; nic se neschválí samo. Řádek, jehož koncept se změnil v poslední minutě, nese tichý štítek „právě se upravuje“; v náhledu ho doplní i přítomnost toho, kdo stránku právě upravuje (jeden živý pohled jen pro otevřený náhled, ne pro každý řádek)."
    >
      <div class="publish-example-row">
        <div><small>Štítek řádku</small><span class="publish-example-live">právě se upravuje</span></div>
        <div><small>Po 409</small><Button size="sm" type="button">Schválit novou verzi</Button></div>
      </div>
    </SettingsSection>
    <SettingsSection
      title="ChangeSummaryField"
      description="Pole samotné, sdílené panelem publikace a schválením konceptu (SpaceDraftApproval). Jeden řádek, nejvýše 500 znaků, počítadlo; pravidlo drží normalizeChangeSummary a pole jen zobrazuje, co dostane."
    >
      <div class="publish-example-fields">
        <div><small>S textem</small><ChangeSummaryField v-model="typed" :error="normalizeChangeSummary(typed).error" /></div>
        <div><small>Chyba validace</small><ChangeSummaryField v-model="tooLong" :error="normalizeChangeSummary(tooLong).error" /></div>
      </div>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.publish-example-row { display: flex; flex-wrap: wrap; align-items: flex-end; gap: 16px; }
.publish-example-row small, .publish-example-fields small { display: block; margin-bottom: 6px; color: var(--muted); font-size: 12px; }
.publish-example-row > * { min-width: 0; max-width: 100%; }
.publish-example-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
.publish-example-bars { display: grid; gap: 14px; }
/* A strip of the page's top bar: its surface, height and line, so the draft's tint reads as in the application. */
.publication-bar { justify-content: flex-end; gap: 8px; min-height: 52px; padding: 8px 16px; border: 1px solid var(--line); border-radius: 8px; background-color: var(--surface); }
.publish-example-status { color: var(--muted); font-size: 13px; }
.publish-example-note { color: var(--muted); font-size: 12.5px; }
/* As SpaceDraftApproval's .approval-live. */
.publish-example-live { color: var(--muted); font-size: 11px; font-style: italic; }
.publish-example-fields { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 24px; }
</style>
