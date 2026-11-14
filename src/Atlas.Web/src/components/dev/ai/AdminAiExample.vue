<script setup lang="ts">
import { computed, onBeforeUnmount, provide, reactive, ref } from "vue";
import { createFakeLiveFeed } from "../../../live/fakeLiveFeed";
import { liveFeedSourceKey } from "../../../live/liveFeed";
import { Switch } from "../../ui/switch";
import { Button } from "../../ui/button";
import ResultBanner from "../../settings/ResultBanner.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import AiIndexFlow from "../../settings/general/ai/AiIndexFlow.vue";
import AiProfileFields from "../../settings/general/ai/AiProfileFields.vue";
import AiModelsCards from "../../settings/general/ai/AiModelsCards.vue";
import type { AiProfileStatus } from "../../settings/general/ai/aiAdmin";
import { provideSettingsRoute } from "../../settings/useSettingsRoute";
import AiAccessSection from "../../settings/general/ai/AiAccessSection.vue";
import AiQuotaFields from "../../settings/general/ai/AiQuotaFields.vue";
import { emptyQuotas } from "../../settings/general/ai/aiAccess";
import type { AiAccessView, Group } from "../../../administration";
import { aiModelDiscoveryKey, mockIndexClient } from "../../settings/general/ai/aiAdminClient";
import { draftFromView, parseDailyLimit, validateDraft, type AiProfileView } from "../../settings/general/ai/aiAdmin";
import { createAiAdminMock } from "../../settings/general/ai/aiAdminMock";

// Administrace → AI (W6). Local data and the in-memory mock of aiAdminMock.ts: no request leaves the gallery. The module itself
// lives at #/settings/general/modules/ai/{overview|embeddings|quick|features|index}. Neither example is a new shared component:
// they compose SettingsSection, ChoiceCards, InlineReveal, SummaryList, ImpactPreview and StepFlow, and show which one to use when.
const mock = createAiAdminMock({ enabled: true, configured: true, activeIndex: true, step: 500 });
provideSettingsRoute("#/gallery");
// The index job's rounds as the server tells them (a fake /hubs/live, JobRunChanged every 3 s): the build flow reads its status on each.
const feed = createFakeLiveFeed();
provide(liveFeedSourceKey, feed.source);
const rounds = setInterval(() => feed.emit("jobs", { runId: 1, jobId: "ai-index.index", state: "running", spaceId: null }), 3000);
onBeforeUnmount(() => clearInterval(rounds));
// The server's warning for a Gateway profile whose server serves another model (GetAiOverviewHandler): still available.
const gatewayWarning = "Server Gateway obsluhuje model „qwen3.8-flash-next-iq3_s“, ale profil má nastavený „qwen3.8-flash-next-iq3_xxs“. AI funguje dál; upravte model v profilu, aby odpovídal.";
const modelStatuses: AiProfileStatus[] = [
  { kind: "chat", state: "Available", model: "qwen3.8-flash-next-iq3_xxs", location: "company", since: null, lastErrorCode: null, lastSuccessAt: null, warning: gatewayWarning },
  { kind: "quick", state: "Available", model: "qwen3.8-flash-next-iq3_xxs", location: "company", since: null, lastErrorCode: null, lastSuccessAt: null, useChatProfile: true, warning: gatewayWarning },
  { kind: "embeddings", state: "Unavailable", model: "text-embedding-bge-m3", location: "company", since: "2026-10-09T19:50:00Z", lastErrorCode: "timeout", lastSuccessAt: null },
  { kind: "rerank", state: "NotConfigured", model: null, location: null, since: null, lastErrorCode: null, lastSuccessAt: null },
];
provide(aiModelDiscoveryKey, async () => ({ ok: true, code: null, models: [{ id: "sample-model", name: "Ukázkový model" }] }));
const accessClient = {
  groups: async () => mock.handle("GET", "/admin/groups").body as Group[],
  aiAccess: async () => structuredClone(mock.handle("GET", "/admin/ai/access").body) as AiAccessView,
  setAiAccess: async (groups: unknown, expectedVersion: string | null) => structuredClone(mock.handle("PUT", "/admin/ai/access", { groups, expectedVersion }).body) as AiAccessView,
};
const personal = reactive(emptyQuotas());
const view = mock.handle("GET", "/admin/ai/profiles/embeddings").body as AiProfileView;
const blank = createAiAdminMock().handle("GET", "/admin/ai/profiles/embeddings").body as AiProfileView;

const chatBlank = createAiAdminMock().handle("GET", "/admin/ai/profiles/chat").body as AiProfileView;
const chatDraft = reactive(draftFromView(chatBlank));
const limit = ref("");
const limitCheck = computed(() => parseDailyLimit(limit.value));
const locked = ref(false);
const validateFields = ref(false);
const embeddingFields = ref<InstanceType<typeof AiProfileFields>>();
function checkFields() { validateFields.value = true; void embeddingFields.value?.focusFirstError(); }
const draft = reactive(draftFromView(blank));
const lockedDraft = reactive(draftFromView(view));
const message = ref("");
const flowKey = ref(0);
const client = mockIndexClient((method, path, body) => mock.handle(method, path, body));
function again(text: string) {
  message.value = text;
  flowKey.value++;
}
</script>

<template>
  <div class="admin-ai-example">
    <SettingsSection title="Modely: sdílený profil a výpadek" description="Postranní přehled používá AiModelsCards. Název modelu se zalamuje, krátký stav a datum výpadku mají vlastní řádek. Rychlé úlohy přebírají model i dostupnost profilu Chat. Chat tu ukazuje varování: server Gateway obsluhuje jiný model, než profil uvádí, profil zůstává dostupný a stav říká „Vyžaduje pozornost“ jako u záloh; text varování s odkazem na úpravu modelu je v sekci „AI potřebuje pozornost“ (AiHealthStatus) nad Modely.">
      <div style="max-width: 300px"><AiModelsCards current="chat" :profiles="modelStatuses" /></div>
    </SettingsSection>
    <SettingsSection title="Administrace → AI: závislá nastavení" description="Dvě skladby, které se nezaměňují. Progresivní odkrývání řeší závislosti mezi poli jednoho formuláře: nejdřív volba poskytovatele, pod ní odkrytá pole, pokročilé volby sbalené s výpisem toho, co platí, a živé shrnutí. StepFlow se používá jen pro oddělené fáze s dlouhou prací mezi nimi, tady pro výměnu generace indexu (Profil → Stavba → Ověření → Aktivace). Uvnitř kroku Profil platí tatáž pole i pravidla jako ve formuláři. Nová sdílená komponenta nevznikla: formulář i tok skládají existující prvky a oba přibyly jen jako součást stránek modulu. Srovnání vloženého formuláře sítě s fázovým průvodcem je v galerii nastavení (Sítě).">
      <ul class="admin-ai-rules">
        <li><strong>Klíč je jen k zápisu.</strong> Formulář ho nikdy nevyplní, uložený klíč nabídne jako „Uložený klíč ✓ · Změnit · Odstranit“.</li>
        <li><strong>Změna poskytovatele.</strong> Zachová model a časové limity, adresu, která patřila předchozímu poskytovateli, vrátí na výchozí a řekne to. Uložený klíč je vázaný na poskytovatele i adresu; při jejich změně se rovnou odkryje pole pro nový klíč.</li>
        <li><strong>Chyba ve sbalené části.</strong> Další nastavení zůstává sbalené, dokud ho nepotřebujete. Při neúspěšném uložení se chybná pole odkryjí a formulář zaměří první chybu. Zkuste časový limit 0, část sbalte a zkontrolujte formulář.</li>
        <li><strong>Zamčený embeddingový model.</strong> Pouze u embeddingů s aktivním indexem jsou model, verze a prefixy jen ke čtení a vede se z nich cesta „Vyměnit model…“ do toku výměny generace.</li>
        <li><strong>Ikony.</strong> Stavy mají ikonu typu. <code>AiGlyph</code> tu není: žádný z prvků nespouští model pro uživatele.</li>
      </ul>
    </SettingsSection>

    <SettingsSection title="Embeddingy: progresivní odkrývání" description="Vyberte poskytovatele a sledujte, co se odkryje a co se při změně zahodí.">
      <div class="admin-ai-switch"><label><Switch v-model="locked" />Aktivní index zamyká model</label></div>
      <AiProfileFields v-if="!locked" ref="embeddingFields" :key="'free'" kind="embeddings" :draft="draft" :view="blank" :errors="validateFields ? validateDraft(draft, blank) : {}" />
      <AiProfileFields v-else :key="'locked'" kind="embeddings" :draft="lockedDraft" :view="view" locked swap-href="#/gallery/ai" />
      <Button v-if="!locked && draft.providerId" type="button" variant="outline" @click="checkFields">Zkontrolovat formulář</Button>
    </SettingsSection>

    <SettingsSection title="Chat a denní limit (fáze 3)" description="Karta Chat je tentýž formulář jako Rychlé úlohy (AiProfileFields kind=chat). Firemní LM Studio a Gateway stojí vedle cloudových OpenAI a Anthropic; Gateway a Anthropic jsou jen pro rychlé úlohy a chat. Čtyři volby používají existující ChoiceCards ve dvou sloupcích, na mobilu pod sebou. Stav firemní nebo cloudový uvidí uživatel u každé odpovědi. Rychlé úlohy dostanou volbu Použít profil Chat, která profil Chat jen půjčí a vlastní pole skryje. Ve Funkcích mají úlohy Odpověď z wiki a Asistent denní limit na uživatele: celé číslo 1 až 10 000, prázdné pole znamená bez limitu.">
      <AiProfileFields kind="chat" :draft="chatDraft" :view="chatBlank" :errors="validateDraft(chatDraft, chatBlank)" />
      <div class="admin-ai-limit">
        <label for="admin-ai-limit-demo">Denní limit na uživatele</label>
        <input id="admin-ai-limit-demo" v-model="limit" class="st-input" inputmode="numeric" placeholder="bez limitu" data-limit-demo />
        <p class="st-note" data-limit-demo-result>{{ limitCheck.error ?? (limitCheck.value === null ? "Bez limitu." : `Nejvýše ${limitCheck.value} odpovědí denně na uživatele.`) }}</p>
      </div>
    </SettingsSection>

    <SettingsSection title="Přístup přes skupiny a osobní limity" description="AiAccessSection používá seznam povolených skupin, SearchableChoiceList pro přidání a jednu SaveBar. AiQuotaFields jsou společná pole pro tuto stránku a osobní výjimku v detailu účtu; samy neukládají ani nepočítají dědění. Na rozdíl od globálního limitu rozlišují převzetí, bez limitu a vlastní číslo; čtvrtý limit patří asistentovi s nástroji. Právo „AI s nástroji“ (D17) nevzniklo jako nová komponenta: u grantu skupiny je to Switch v SettingsRow pod tlačítkem Upravit (nový grant ho má zapnutý, vypnutý se ukáže ve shrnutí řádku), v detailu účtu výběr Výchozí (povoleno, pokud ho skupiny nevypnou) / Osobně povolit / Osobně zakázat, který InlineReveal odkryje pod „Přístup k modelovým funkcím“, jen dokud AI není osobně zakázaná. Načíst modely v profilech pracuje s místním katalogem; ruční ID zůstává možné. Chat lze vyměnit bez přestavby indexu.">
      <AiAccessSection :client="accessClient" />
      <SettingsSection title="Osobní výjimka: limity" advanced summary="Podle skupin a wiki"><AiQuotaFields :draft="personal" inherit-label="Podle skupin a wiki" /></SettingsSection>
    </SettingsSection>

    <SettingsSection title="Výměna generace indexu: StepFlow" description="Profil, Stavba, Ověření, Aktivace. Stavba běží na pozadí a tok lze opustit a vrátit se k němu. Aktivace je jediný krok, který mění, nad čím hledání běží, a chce prošlé ověření. Ukázka běží nad pamětí; skutečné koncové body přijdou s balíkem V2.">
      <ResultBanner v-if="message" :message="message" @dismiss="message = ''" />
      <AiIndexFlow :key="flowKey" mode="replace" :profile="view" :client="client" @done="again" @leave="again('Tok se zavřel, stavba by pokračovala na pozadí.')" />
      <Button v-if="message" size="sm" variant="outline" type="button" @click="again('')">Začít znovu</Button>
    </SettingsSection>
  </div>
</template>

<style scoped>
.admin-ai-example { display: grid; gap: 14px; min-width: 0; }
.admin-ai-rules { display: grid; gap: 6px; margin: 0; padding-left: 18px; font-size: 13px; line-height: 1.55; }
.admin-ai-limit { display: grid; gap: 6px; max-width: 280px; font-size: 13px; }
.admin-ai-switch label { display: inline-flex; align-items: center; gap: 8px; font-size: 13px; }
</style>
