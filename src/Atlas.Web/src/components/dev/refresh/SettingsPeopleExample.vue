<script setup lang="ts">
import type { DirectoryUser } from "../../../administration";
import type { PrincipalAccess } from "../../settings/access/accessModel";
import PersonAccount, { type PersonAccountSource } from "../../settings/people/PersonAccount.vue";
import "../../settings/settings.css";

// Refresh 2.5, settings (board 09): the person beside the table of people. Local data only.
const account: DirectoryUser = { id: "u-jana", username: "jana.novakova", displayName: "Jana Nováková", isActive: true, isWikiAdmin: true, version: "v1", hasBuiltinCredential: true, source: "builtin", credentialState: "active" };
const preview: PrincipalAccess = {
  principal: { kind: "user", id: "u-jana", name: "Jana Nováková", detail: "jana.novakova", isActive: true, isWikiAdmin: true, lastSignInAt: "2026-10-09T07:00:00Z", groups: [{ id: "g-platform", name: "Vývoj – Platforma" }, { id: "g-qa", name: "QA" }] },
  scope: "wiki",
  canManage: true,
  entries: [
    { kind: "space", resourceId: "hp", title: "Care Portal", level: "write", sources: [{ via: "group", label: "Vývoj – Platforma", level: "write", effective: true }] },
    { kind: "space", resourceId: "runbooks", title: "Runbooky a provoz", level: "read", sources: [{ via: "group", label: "QA", level: "read", effective: true }] },
    { kind: "space", resourceId: "wiki", title: "Modelsoft wiki", level: "manage", sources: [{ via: "automatic", label: "Správce wiki", level: "manage", effective: true }] },
    { kind: "vault", resourceId: "v-1", title: "Servery", spaceId: "runbooks", level: "read", sources: [{ via: "direct", label: "Přímo", level: "read", effective: true }] },
  ],
};
const spaces = [{ id: "hp", color: "violet" }, { id: "runbooks", color: "coral" }, { id: "wiki", color: "blue" }];
// A wiki administrator looking (the part shows nobody else anything); creating answers with a made-up activation code.
const source: PersonAccountSource = {
  me: async () => ({ id: "u-other", isWikiAdmin: true }),
  account: async () => account,
  principal: async () => preview,
  create: async () => ({ token: "UKAZKA-1234", expiresAt: "2026-12-31T00:00:00Z", purpose: "invitation" }),
};
</script>

<template>
  <div class="settings-ui settings-people-example" data-settings-people-example>
    <p class="st-note">PersonAccount (people/PersonAccount.vue): část Účet v profilu člověka (ContentDetail kontaktu), jen pro správce wiki – stav účtu, skupiny a přístup k prostorům, a cesta do plné karty (Přístupy, Účet, Skupiny, Tokeny, Činnost). Nahradila samostatný panel PersonPeek: klik na jméno v Lidé otevře profil člověka (jeho kontakt) se stejnou částí, jeden profil pro všechny. Kontakt bez účtu nabídne „Vytvořit účet“: pozvánka z Lidé (InviteAccountWizard) se jménem kontaktu, nový účet se s kontaktem propojí v témže požadavku. Ostatní lidé část nevidí a správcovské endpointy se neptají.</p>
    <div class="settings-people-example-peek"><PersonAccount account-id="u-jana" :contact="{ id: 'c-jana', name: 'Jana Nováková' }" :spaces="spaces" :source="source" /></div>
    <div class="settings-people-example-peek"><PersonAccount :contact="{ id: 'c-ota', name: 'Ota Dodavatel' }" :source="source" /></div>
  </div>
</template>

<style scoped>
.settings-people-example { display: grid; gap: 14px; }
.settings-people-example-peek { max-width: 360px; padding: 4px 16px 16px; border: 1px solid var(--line); border-radius: var(--st-radius); background: var(--surface); }
</style>
