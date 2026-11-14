<script setup lang="ts">
import { ref } from "vue";
import type { DirectoryUser, Group, Invitation } from "../../administration";
import ResultBanner from "../settings/ResultBanner.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import AccountStatusBadge from "../settings/people/AccountStatusBadge.vue";
import CreateGroupWizard from "../settings/people/CreateGroupWizard.vue";
import InviteAccountWizard from "../settings/people/InviteAccountWizard.vue";
import type { InviteDraft } from "../settings/people/types";

// Gallery examples of the Lidé composites (components/settings/people): local sample data, no API, nothing persisted.
const result = ref("");
const inviteKey = ref(0);
const groupKey = ref(0);
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
const states: Pick<DirectoryUser, "isActive" | "credentialState">[] = [
  { isActive: true, credentialState: "active" },
  { isActive: true, credentialState: "pending" },
  { isActive: false, credentialState: "active" },
];

async function invite(draft: InviteDraft): Promise<Invitation> {
  await wait(400);
  if (draft.username.includes("chyba")) throw new Error("Ukázka odmítnutí: uživatelské jméno server nepřijal.");
  return {
    token: "UKAZKA-7QK4-M2XP-91TD",
    expiresAt: new Date(Date.now() + 7 * 86_400_000).toISOString(),
    purpose: "invitation",
    user: { id: "demo", username: draft.username, displayName: draft.displayName, isActive: true, isWikiAdmin: draft.isWikiAdmin, version: "v1", hasBuiltinCredential: true, source: "builtin", credentialState: "pending" },
  };
}
async function createGroup(name: string): Promise<Group> {
  await wait(400);
  if (name.toLocaleLowerCase("cs").includes("chyba")) throw new Error("Ukázka odmítnutí: skupinu se nepodařilo založit.");
  return { id: "demo-group", name, version: "v1", source: "local" };
}
</script>

<template>
  <SettingsPage title="Lidé" description="Sestavy pro účty a skupiny ve Správě wiki (components/settings/people). Data a volání jsou ukázková; stránky Účty a Skupiny jim předávají skutečné API.">
    <ResultBanner v-if="result" :message="result" @dismiss="result = ''" />

    <SettingsSection title="AccountStatusBadge" description="people/AccountStatusBadge.vue — stav účtu (Aktivní / Čeká na aktivaci / Deaktivovaný) z isActive a credentialState. Neaktivovaný účet se nikdy neukáže jako aktivní. Pro stav účtu ho používejte místo ručně skládaného StatusBadge; pro ostatní stavy je StatusBadge.">
      <div class="st-actions"><AccountStatusBadge v-for="(state, index) in states" :key="index" :account="state" size="md" /></div>
    </SettingsSection>

    <SettingsSection title="InviteAccountWizard" description="people/InviteAccountWizard.vue — Pozvat člověka: platné údaje ihned rozbalí roli a souhrn, předchozí pole zůstávají upravitelná. Pozvánku vytvoří až výslovné potvrzení; poté se ukáže jednorázový aktivační kód přes OneTimeSecret. Obsazená uživatelská jména odmítne už v prvním kroku; uživatelské jméno s „chyba“ ukáže odmítnutí na souhrnu. Pro přidání existujícího člověka k prostoru nebo trezoru je GrantFlow.">
      <InviteAccountWizard
        :key="inviteKey"
        :submit="invite"
        :taken-usernames="['jana', 'petr']"
        @done="result = `Účet ${$event.user?.displayName} založen (jen v galerii).`; inviteKey++"
        @cancel="inviteKey++"
      />
    </SettingsSection>

    <SettingsSection title="CreateGroupWizard" description="people/CreateGroupWizard.vue — Nová skupina: platný název ihned odkryje souhrn, jeho smazání souhrn ponechá a zablokuje uložení; členy přidává detail skupiny přes SearchableChoiceList. Název „Provoz“ je obsazený, název s „chyba“ ukáže odmítnutí. Navazující rozhodnutí sjednocuje ProgressiveFlow; StepFlow používejte jen pro skutečně oddělené fáze se Zpět/Další.">
      <CreateGroupWizard
        :key="groupKey"
        :submit="createGroup"
        :taken-names="['Provoz']"
        @done="result = `Skupina ${$event.name} založena (jen v galerii).`; groupKey++"
        @open="result = `Otevřít skupinu ${$event.name} (jen v galerii).`; groupKey++"
        @cancel="groupKey++"
      />
    </SettingsSection>
  </SettingsPage>
</template>
