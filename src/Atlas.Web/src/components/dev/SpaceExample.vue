<script setup lang="ts">
import { defineComponent, h, reactive, ref } from "vue";
import type { SpacePinPolicy } from "../../api";
import type { CatalogDefinition } from "../../projector/projectorApi";
import { provideSettingsContext } from "../settings/context";
import SpaceProfileFields from "../settings/space/SpaceProfileFields.vue";
import SpaceSetsPage from "../settings/space/SpaceSetsPage.vue";
import ConflictNote from "../settings/ConflictNote.vue";
import ResultBanner from "../settings/ResultBanner.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import GrantValidityFields from "../settings/GrantValidityFields.vue";
import { validityError } from "../settings/grantValidity";
import ResourceAccessPanel from "../settings/access/ResourceAccessPanel.vue";
import { levelLabel } from "../settings/access/accessKinds";
import type { AccessChangeDraft, AccessSource, ResourceAccess, ResourceAccessView } from "../settings/access/accessModel";
import type { AccessRow } from "../settings/access/accessView";

// Gallery examples of the space settings composites: local sample data, no API, nothing persisted.
const result = ref("");
const direct = (level: AccessSource["level"], extra: Partial<AccessSource> = {}): AccessSource => ({ via: "direct", label: "", level, version: "1", startAt: null, expiresAt: null, effective: true, ...extra });
const access = ref<ResourceAccess>({
  resource: { kind: "space", id: "provoz", title: "Provoz" },
  canManage: true,
  automatic: [{ label: "Správci wiki", detail: "automaticky ve všech prostorech, i k soukromým konceptům", level: "manage", count: 2 }],
  entries: [
    { subject: { kind: "user", id: "u-1", name: "Jana Nováková", detail: "jana" }, level: "manage", sources: [direct("manage")] },
    { subject: { kind: "group", id: "g-1", name: "Provoz tým", memberCount: 12 }, level: "write", sources: [direct("write", { expiresAt: "2026-12-31T23:00:00Z" })] },
    { subject: { kind: "user", id: "u-2", name: "Petr Svoboda", detail: "petr" }, level: "read", sources: [direct("read", { startAt: "2030-01-01T08:00:00Z", effective: false })] },
  ],
  hasMore: false,
});
const view = ref<ResourceAccessView>("effective");
const editing = ref<string | null>(null);
function change(row: AccessRow, _grant: AccessSource, draft: AccessChangeDraft) {
  access.value = {
    ...access.value,
    entries: access.value.entries.map((entry) =>
      `${entry.subject.kind}:${entry.subject.id}` === row.key ? { ...entry, level: draft.level, sources: entry.sources.map((source) => (source.via === "direct" ? { ...source, level: draft.level, startAt: draft.startAt, expiresAt: draft.expiresAt } : source)) } : entry,
    ),
  };
  editing.value = null;
  result.value = `${row.title}: ${levelLabel(draft.level)} (jen v galerii).`;
}

// SpaceProfileFields in the deferred mode of Nový prostor: the field itself keeps the choice.
const profile = reactive({ name: "Provoz", description: "Runbooky a pohotovost.", icon: "server", color: "teal-2", pinPolicy: "None" as SpacePinPolicy });

const startAt = ref("");
const expiresAt = ref("2026-12-31T18:00");
const conflict = ref(true);

// Sady a katalog with sample data: two catalog pages (each row its page's title from the space's tree) and two sets.
const definition = (key: string, pageId: string, product: string, category: string, bundle: string): CatalogDefinition =>
  ({ key, title: key, titleMarkdown: key, direction: "inbound", requires: [], pageId, bundle, moduleKey: bundle, category, product });
const catalogDefinitions = [
  definition("vpn", "cat-portal", "Portál pacienta", "Přístup", "VPN"), definition("ssl", "cat-portal", "Portál pacienta", "Přístup", "Certifikáty"),
  definition("sms", "cat-portal", "Portál pacienta", "Notifikace", "SMS brána"), definition("nia", "cat-identity", "Identita", "Přihlášení", "NIA"),
];
const SetsShell = defineComponent({
  setup() {
    provideSettingsContext({
      userId: "u-admin", isWikiAdmin: true, spaces: [], backupWarning: null, version: undefined, imported: () => {}, warning: () => {},
      openPage: pageId => { result.value = `Otevřít stránku ${pageId} (jen v galerii).`; }, newTemplate: () => {}, newPage: () => {},
      spaceChanged: () => {}, spaceTrashed: () => {}, spacesChanged: () => {}, accessChanged: () => {},
    });
    const api = {
      catalog: async () => ({ definitions: catalogDefinitions, warnings: [] }),
      presets: async () => [
        { pageId: "set-standard", title: "Standardizace", requirements: 12, summary: "Typická zakázka nemocnice: portál, přihlášení přes NIA a notifikace.", products: ["Portál pacienta", "Identita"], modules: 4, differs: ["SMS brána"] },
        { pageId: "set-small", title: "Malá ordinace", requirements: 3, summary: "", products: ["Portál pacienta"], modules: 1 },
      ],
    };
    const tree = async () => ({ version: 1, items: [{ id: "cat-portal", title: "Katalog: Portál pacienta" }, { id: "cat-identity", title: "Katalog: Identita" }] }) as never;
    return () => h("div", { class: "settings-ui" }, h(SpaceSetsPage, { spaceId: "gallery", api, tree }));
  },
});
</script>

<template>
  <SettingsPage title="Nastavení prostoru" description="Sestavy pro Obecné, Sdílení a Přístupy prostoru (components/settings/space). Data a volání jsou ukázková; stránky nastavení jim předávají skutečné API.">
    <ResultBanner v-if="result" :message="result" @dismiss="result = ''" />

    <SettingsSection title="SpaceProfileFields" description="settings/space/SpaceProfileFields.vue — co prostor je: název, popis, ikona a barva (AppearancePicker), obrázek (SpaceCoverField) a připnutí pro všechny. Jedna sada polí pro Nový prostor (CreateSpaceForm) i Obecné v Nastavení prostoru (SpaceGeneralPage), takže obě místa vypadají a kontrolují stejně; ukládání si drží každé zvlášť (Vytvořit prostor, nebo SaveBar s CAS). Obrázek se v Obecné ukládá hned, v Novém prostoru (deferred, jako zde) se jen podrží a uloží se se založením. Volte ji pro každé místo, kde se zadávají údaje prostoru; přístupy prostoru jsou ResourceAccessPanel.">
      <div class="settings-ui gallery-space-profile">
        <SpaceProfileFields v-model:name="profile.name" v-model:description="profile.description" v-model:icon="profile.icon" v-model:color="profile.color" v-model:pin-policy="profile.pinPolicy" space-id="gallery" deferred />
      </div>
    </SettingsSection>

    <SettingsSection title="SpaceSetsPage" description="settings/space/SpaceSetsPage.vue — Sady a katalog (modul Nasazení): kde jsou katalog a sady prostoru a co znamenají, jen ke čtení. Řádek katalogu nese název své stránky (ze stromu prostoru; dokud není známý, „Katalog požadavků“), počty požadavků, produktů a modulů a štítky produktů; řádek sady její první odstavec, moduly a produkty a upozornění, když text požadavku v sadě není jako v katalogu. Otevřít vede na stránku, vše se upravuje v jejím textu. Skládá SettingsList, SettingsListRow, StatusBadge, LoadingRegion a EmptyState; nová je jen stránka, žádný nový prvek. Data jsou ukázková.">
      <SetsShell />
    </SettingsSection>

    <SettingsSection title="Přístupy prostoru" description="access/ResourceAccessPanel.vue s druhem space — kdo má přístup k prostoru: zamčený řádek správců wiki, skupiny a lidé s úrovní na schodišti Čte → Upravuje → Spravuje a platností, úprava úrovně i platnosti na místě s explicitním uložením a odebrání. Hledání zúží načtené řádky hned; když server seznam prostoru ořízl na 200 (hasMore), pošle ho ResourceAccess po pauze v psaní i serveru (q), takže najde kohokoli. Tentýž panel slouží stránkám, trezorům i heslům; další ukázky jsou v Bezpečnostním centru.">
      <ResourceAccessPanel v-model:view="view" :access="access" :editing="editing" @add="result = 'Přidat přístup: GrantFlow na adrese access/new (jen v galerii).'" @change="editing = editing === $event.key ? null : $event.key" @cancel="editing = null" @save="change" @revoke="result = `Odebrat ${$event.title}? Stránka se zeptá přes ConfirmDialog (jen v galerii).`" />
    </SettingsSection>

    <SettingsSection title="GrantValidityFields" description="settings/GrantValidityFields.vue — jediná komponenta platnosti přidělení: nepovinné Od a Do a dlaždice 7 / 30 / 90 dní / Bez omezení (konec daný počet dní od začátku, nebo žádný). Vlastní datum dlaždici zruší. Používá ji krok Platnost v GrantFlow i úprava přidělení na místě; pravidla a kontrola jsou v grantValidity.ts: rodič kontroluje průběžně (validityError), důvod stojí pod daty a Uložit je do opravy vypnuté. Zkuste konec v minulosti.">
      <GrantValidityFields v-model:start-at="startAt" v-model:expires-at="expiresAt" :error="validityError(startAt, expiresAt) || undefined" />
    </SettingsSection>

    <SettingsSection title="ConflictNote" description="settings/ConflictNote.vue — někdo změnil totéž mezitím (409), řečeno u toho, co selhalo. Okamžitá akce (grant, přepínač) už načetla aktuální stav a jen to oznámí. Stránka s konceptem (SaveBar) koncept nikdy nenahradí: nabídne „Načíst znovu“, které ho zahodí. Pro chybu s opakováním je ErrorState, pro úspěch ResultBanner.">
      <ConflictNote v-if="conflict" text="Přístupy mezitím změnil někdo jiný. Načetli jsme aktuální stav; zkontrolujte ho a změnu případně zopakujte." @dismiss="conflict = false" />
      <p v-else class="st-note"><button type="button" class="st-link" @click="conflict = true">Zobrazit znovu</button></p>
      <ConflictNote text="Prostor mezitím upravil někdo jiný. Vaše změny zůstaly neuložené; Načíst znovu je zahodí a ukáže aktuální hodnoty." reload-label="Načíst znovu" :dismissible="false" />
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.gallery-space-profile { display: grid; gap: 18px; min-width: 0; }
</style>
