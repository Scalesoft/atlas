<script setup lang="ts">
import { ref } from "vue";
import PersonalApiTokens, { type TokenSource } from "../auth/PersonalApiTokens.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import ApiLevelPicker from "../settings/people/ApiLevelPicker.vue";
import ApiTokenList from "../settings/people/ApiTokenList.vue";
import type { AdminApiToken } from "../settings/access/accessModel";
import type { ApiLevel } from "../../apiTokenRights";

// Gallery examples of the personal API token rights (Atlas 2.0 MCP track, W1): local sample data, no API, nothing persisted.
// PersonalApiTokens takes its data through `source`, so these are the real profile component, not a copy of it.
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
const inDays = (days: number) => new Date(Date.now() + days * 86_400_000).toISOString();
const ago = (days: number) => new Date(Date.now() - days * 86_400_000).toISOString();

type SampleToken = Awaited<ReturnType<TokenSource["list"]>>["tokens"][number];
function sample(level: string, mcpEnabled: boolean, tokens: SampleToken[]): TokenSource {
  let list = tokens;
  return {
    list: async () => {
      await wait(150);
      return { canIssueTokens: level !== "none", permissionVersion: "demo", tokens: list, level, mcpEnabled, mcpUrl: mcpEnabled ? `${location.origin}/mcp` : null, apiBaseUrl: "/api/v1", openApiUrl: "/api/v1/openapi.json" };
    },
    issue: async (body) => {
      await wait(400);
      const token = { id: `demo-${list.length + 1}`, name: body.name, prefix: "atl_demo", scopes: body.scopes, channels: body.channels, effectiveScopes: body.scopes, usable: body.channels.includes("rest") || mcpEnabled, activeFrom: new Date().toISOString(), expiresAt: inDays(body.expiresInDays), activeTo: null };
      list = [token, ...list];
      return { token, secret: "UKAZKA-atl_demo-7QK4M2XP91TDQ8WZ" };
    },
    revoke: async (id) => {
      await wait(250);
      list = list.map((token) => (token.id === id ? { ...token, activeTo: new Date().toISOString(), usable: false } : token));
    },
  };
}
const base = { prefix: "atl_demo", activeFrom: ago(20), expiresAt: inDays(70), activeTo: null };
const full = sample("write", true, [
  { ...base, id: "f1", name: "Nasazení dokumentace", scopes: ["read", "write"], channels: ["rest"], effectiveScopes: ["read", "write"], usable: true },
  { ...base, id: "f2", name: "Claude Code – notebook", scopes: ["read"], channels: ["mcp"], effectiveScopes: ["read"], usable: true },
]);
const trimmed = sample("read", true, [
  { ...base, id: "t1", name: "Noční export", scopes: ["read", "write"], channels: ["rest"], effectiveScopes: ["read"], usable: true },
]);
const noModule = sample("write", false, [
  { ...base, id: "m1", name: "Claude Code – notebook", scopes: ["read", "write"], channels: ["mcp"], effectiveScopes: ["read", "write"], usable: false },
]);
const adminTokens: AdminApiToken[] = [
  { id: "a1", userId: "u1", userName: "Jana Nováková", name: "Nasazení dokumentace", scopes: ["read", "write"], channels: ["rest"], createdAt: ago(20), expiresAt: inDays(70), lastUsedAt: null, revokedAt: null },
  { id: "a2", userId: "u2", userName: "Petr Svoboda", name: "Claude Code", scopes: ["read"], channels: ["mcp"], createdAt: ago(5), expiresAt: inDays(25), lastUsedAt: null, revokedAt: null },
];
const level = ref<ApiLevel>("write");
const saved: ApiLevel = "write";
</script>

<template>
  <SettingsPage title="Osobní API a tokeny" description="Práva osobních tokenů: úroveň účtu jako strop, rozsah Čtení, nebo Zápis, a kanály REST API a MCP. Zápis je živá úprava jako u člověka, v mezích práv vlastníka; co uvidí čtenáři, určuje publikace stránky. Data a volání jsou ukázková. MCP není funkce AI, proto tu není ikona AI.">
    <SettingsSection title="ApiLevelPicker" description="settings/people/ApiLevelPicker.vue — strop osobního API jednoho účtu (Správa wiki › Lidé › karta › Účet). Volba Bez API upozorní, že vydané tokeny se trvale zneplatní; snížení na nižší úroveň jen ořízne rozsah tokenů a ty zůstanou platné. Ukládá nadřazený formulář (SaveBar), ne tato komponenta. Pro krátkou volbu bez dopadu je OptionPills; tato komponenta k ní přidává popis úrovně a dopad na vydané tokeny.">
      <ApiLevelPicker v-model="level" :saved="saved" />
    </SettingsSection>

    <SettingsSection title="PersonalApiTokens — plná úroveň" description="auth/PersonalApiTokens.vue — Profil › Osobní API. Úroveň Zápis nabídne oba rozsahy, Čtení a Zápis, každý s jednořádkovým vysvětlením; kanály REST API a MCP s alespoň jedním; po vytvoření tokenu pro MCP se ukáže adresa serveru. Vyšší rozsah, než povoluje strop, se nenabízí vůbec.">
      <PersonalApiTokens :source="full" />
    </SettingsSection>

    <SettingsSection title="PersonalApiTokens — token oříznutý stropem" description="Správce snížil úroveň na Čtení: token s vlastním rozsahem Zápis zůstává platný, ale řádek říká „Omezeno správcem na čtení“. Nový token nabídne jen Čtení.">
      <PersonalApiTokens :source="trimmed" />
    </SettingsSection>

    <SettingsSection title="PersonalApiTokens — MCP bez zapnutého modulu" description="Kanál MCP jde zvolit i dřív, než správce zapne modul; volba to vysvětluje. Token jen s MCP je do té doby „Nepoužitelný“ a řádek říká proč.">
      <PersonalApiTokens :source="noModule" />
    </SettingsSection>

    <SettingsSection title="ApiTokenList — pohled správce" description="settings/people/ApiTokenList.vue — všechny tokeny wiki s rozsahem a kanály. Správce tokeny jen zneplatňuje; rozsah ani kanál tokenu nemění.">
      <ApiTokenList :tokens="adminTokens" />
    </SettingsSection>
  </SettingsPage>
</template>
