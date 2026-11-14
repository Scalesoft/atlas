<script setup lang="ts">
import type { DirectoryUser } from "../../../administration";
import type { PrincipalAccess } from "../../settings/access/accessModel";
import PersonPeek from "../../settings/people/PersonPeek.vue";
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
</script>

<template>
  <div class="settings-ui settings-people-example" data-settings-people-example>
    <p class="st-note">PersonPeek (people/PersonPeek.vue, soukromá část stránky Lidé): na širokém displeji otevře klik na jméno v tabulce lidí náhled vedle tabulky – stav, skupiny a přístup k prostorům – a vede do plné karty člověka (Přístupy, Účet, Skupiny, Tokeny, Činnost). Změny se dělají jen na kartě; jméno zůstává odkazem na kartu pro novou kartu prohlížeče, klávesnici i úzké displeje. Pro plný přehled přístupů je PrincipalAccessEditor na kartě.</p>
    <div class="settings-people-example-peek"><PersonPeek :account="account" current-user-id="u-other" :spaces="spaces" :preview="preview" /></div>
  </div>
</template>

<style scoped>
.settings-people-example { display: grid; gap: 14px; }
.settings-people-example-peek { max-width: 360px; }
</style>
