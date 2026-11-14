<script setup lang="ts">
import t from "../../i18n/contacts-ui";
import { computed, nextTick, onMounted, ref } from "vue";
import { errorMessage } from "../../api";
import { useToast } from "../../composables/useToast";
import { contactApi, type AccountContactSuggestion, type ContactApi, type ContactPair } from "../../contacts/contactApi";
import { mergeOverCapacity, mergedContact, type ContactData, type ContactField } from "../../contacts/contactData";
import type { ActionMenuItem } from "../ActionMenu.vue";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import RowActionsMenu from "../RowActionsMenu.vue";
import ConfirmDialog from "../settings/ConfirmDialog.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import OptionPills from "../settings/OptionPills.vue";
import SummaryList from "../settings/SummaryList.vue";
import { Button } from "../ui/button";

/**
 * Kontakty → Možné duplicity (spec 2026-10-30 §5): pairs of directory contacts with a shared weak key (phone, name with organization)
 * or a similar name, each with why, and „Sloučit“: the confirmation picks the contact that stays and shows the fields it will have
 * (mergedContact); the other one goes to the trash and its uses follow. Esc and Zpět na adresář go back; `merged` names the contact
 * that went, so the directory reloads. Like ContactTrash, it swaps itself for the directory; `api` is replaced by the gallery.
 * Above them, for a wiki admin only (owner decision R3: every account has one contact), „Propojit účty s kontakty“: an account and a
 * contact that may be the same person, with the evidence; „Propojit“ makes the contact the account's (the contact the account has is merged
 * into it), „Není to on“ gives the account its own contact, so none of its pairs is offered again (the decision is in the data, nothing
 * else is stored), with Zpět in its toast; both also from the row's „Co s tím jde dělat“ (RowActionsMenu).
 */
const props = defineProps<{ api?: Pick<ContactApi, "duplicates" | "merge" | "accountSuggestions" | "resolveAccount"> }>();
const emit = defineEmits<{ close: []; merged: [sourceId: string] }>();
const source = computed(() => props.api ?? contactApi);

const pairs = ref<ContactPair[]>([]);
const accounts = ref<AccountContactSuggestion[]>([]);
const loading = ref(true), error = ref("");
const root = ref<HTMLElement>();
async function load() {
  loading.value = true; error.value = "";
  // Both at once and shown together: the account list never pushes the pairs down after they appeared.
  try { const [found, suggested] = await Promise.all([source.value.duplicates(), source.value.accountSuggestions()]); pairs.value = found.pairs; accounts.value = suggested.items; }
  catch (failure) { error.value = errorMessage(failure); }
  finally { loading.value = false; }
}
onMounted(load);

const reasons: Record<string, string> = { phone: t.duplicates.phone, name: t.duplicates.name, similar: t.duplicates.similar };
const reasonOf = (pair: ContactPair) => reasons[pair.reason] ?? pair.reason;
const lineOf = (data?: ContactData) => [data?.organization, data?.emails?.[0], data?.phones?.[0]].filter(Boolean).join(" · ");

const toaster = useToast();
const accountActions: ActionMenuItem[] = [{ id: "link", label: t.duplicates.link, icon: "link" }, { id: "dismiss", label: t.duplicates.dismiss, icon: "x" }];
const keyOf = (item: AccountContactSuggestion) => `${item.accountId}:${item.contact.contactId}`;
// The username tells two accounts of one name apart (P8) when there is no address to do it.
const accountLine = (item: AccountContactSuggestion) => [t.duplicates.account, item.accountEmail || item.accountUsername || t.duplicates.noEmail, !item.accountActive && t.duplicates.deactivated].filter(Boolean).join(" · ");
/** Why the pair is offered: the same name with the address missing or different, or one of several contacts of the account's address. */
function evidenceOf(item: AccountContactSuggestion) {
  if (item.reason === "email") return t.duplicates.sameEmail;
  const emails = item.contact.contact?.emails ?? [];
  return t.duplicates.sameName(!item.accountEmail ? t.duplicates.accountNoEmail : !emails.length ? t.duplicates.contactNoEmail : t.duplicates.otherEmail);
}
const deciding = ref("");
async function decide(item: AccountContactSuggestion, decision: string) {
  if (deciding.value || (decision !== "link" && decision !== "dismiss")) return;
  const at = accounts.value.indexOf(item);
  deciding.value = keyOf(item);
  try {
    const result = await source.value.resolveAccount(item.accountId, item.contact.contactId, decision);
    if (decision === "link") {
      // The account is decided: its other candidates go too, and so does the contact for other accounts (it is this one's now);
      // a merged contact of the account reloads the directory as a merge does.
      accounts.value = accounts.value.filter(other => other.accountId !== item.accountId && other.contact.contactId !== item.contact.contactId);
      if (item.ownContactId) emit("merged", item.ownContactId);
      toaster.success(t.duplicates.linked(item.accountName, item.contact.name));
    } else {
      // The account has its own contact now: none of its pairs is offered any more.
      const gone = accounts.value.flatMap((other, index) => (other.accountId === item.accountId ? [{ other, index }] : []));
      accounts.value = accounts.value.filter(other => other.accountId !== item.accountId);
      const created = result.createdContactId;
      toaster.success(t.duplicates.ownContact(item.accountName), created ? { undo: () => void restore(item, gone, created) } : undefined);
    }
    deciding.value = "";
    await nextTick();
    // The next row takes the focus (enabled again), after the last one the row above, else the first action left on the page; a row
    // still leaving is not one of them.
    const staying = (selector: string) => [...(root.value?.querySelectorAll<HTMLElement>(selector) ?? [])].filter(node => !node.closest(".contact-row-leave-active"));
    const links = staying("[data-account-link]");
    (links[Math.min(at, links.length - 1)] ?? staying("[data-contact-merge], [data-contact-duplicates-back]")[0])?.focus();
  } catch (failure) {
    toaster.error(errorMessage(failure));
  } finally {
    deciding.value = "";
  }
}
/** Zpět of „Není to on“: the contact it created goes to the trash, and the account's pairs are offered again where they were. */
async function restore(item: AccountContactSuggestion, gone: { other: AccountContactSuggestion; index: number }[], created: string) {
  try {
    await source.value.resolveAccount(item.accountId, created, "restore");
    for (const { other, index } of gone) accounts.value.splice(Math.min(index, accounts.value.length), 0, other);
    // Zpět left with its toast: the keyboard goes to the pair that is back.
    await nextTick();
    root.value?.querySelector<HTMLElement>(`[data-account-suggestion="${CSS.escape(keyOf(item))}"] [data-account-link]`)?.focus();
  } catch (failure) {
    toaster.error(errorMessage(failure));
  }
}

/** Two same-named contacts differ by what the row line says; with nothing more to say, „první“ / „druhý“ does. */
const pillOf = (side: ContactPair["a"], ordinal: string) => [side.name, lineOf(side.contact) || ordinal].join(" · ");
const merging = ref<ContactPair | null>(null), kept = ref("a"), busy = ref(false), mergeError = ref("");
const target = computed(() => (merging.value ? (kept.value === "a" ? merging.value.a : merging.value.b) : null));
const other = computed(() => (merging.value ? (kept.value === "a" ? merging.value.b : merging.value.a) : null));
const labels: [ContactField, string, string][] = [["name", t.card.name, "user"], ["title", t.card.title, "briefcase"], ["organization", t.card.organization, "building"],
  ["department", t.card.department, "users"], ["emails", t.card.emails, "mail"], ["phones", t.card.phones, "phone"], ["website", t.card.website, "globe"], ["address", t.card.address, "map-pin"],
  ["aliases", t.card.aliases, "hash"]];
const targetData = computed(() => (target.value ? target.value.contact ?? { name: target.value.name } : null));
const otherData = computed(() => (other.value ? other.value.contact ?? { name: other.value.name } : null));
/** A list the server would refuse to overfill: said instead of a result the merge cannot have, and the merge is not offered. */
const overCapacity = computed(() => (targetData.value && otherData.value ? mergeOverCapacity(targetData.value, otherData.value) : null));
const result = computed(() => {
  if (!targetData.value || !otherData.value) return [];
  const merged = mergedContact(targetData.value, otherData.value);
  return labels.flatMap(([key, label, icon]) => { const value = merged[key]; const text = Array.isArray(value) ? value.join(", ") : value; return text ? [{ key, label, icon, value: text }] : []; });
});
function ask(pair: ContactPair) { merging.value = pair; kept.value = "a"; mergeError.value = ""; }
async function merge() {
  if (!target.value || !other.value) return;
  busy.value = true; mergeError.value = "";
  try {
    await source.value.merge(other.value.contactId, target.value.contactId);
    const gone = other.value.contactId;
    pairs.value = pairs.value.filter(pair => pair.a.contactId !== gone && pair.b.contactId !== gone);
    merging.value = null;
    emit("merged", gone);
  } catch (failure) {
    mergeError.value = errorMessage(failure);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <section ref="root" class="contact-duplicates" aria-labelledby="contact-duplicates-title" :aria-busy="loading" @keydown.esc="!($event.target as Element).closest('dialog') && emit('close')">
    <header class="contact-duplicates-header">
      <h3 id="contact-duplicates-title">{{ t.duplicates.title }}</h3>
      <Button variant="outline" type="button" data-contact-duplicates-back @click="emit('close')"><AtlasIcon name="arrow-left" :size="15" />{{ t.duplicates.back }}</Button>
    </header>
    <ErrorState v-if="error" :title="t.duplicates.loadFailed" :text="error" :retry-label="t.duplicates.reload" :busy="loading" @retry="load" />
    <LoadingSkeleton v-else-if="loading && !pairs.length && !accounts.length" :label="t.duplicates.loading" />
    <EmptyState v-else-if="!pairs.length && !accounts.length" icon="users" :title="t.duplicates.none" :text="t.duplicates.noneText" />
    <section v-if="!error && accounts.length" class="contact-duplicates-group" aria-labelledby="account-contacts-title">
      <h4 id="account-contacts-title">{{ t.duplicates.accountsTitle }}</h4>
      <p class="contact-duplicates-help">{{ t.duplicates.accountsHelp }}</p>
      <TransitionGroup tag="ul" name="contact-row" class="contact-duplicates-items" :aria-label="t.duplicates.accountsList">
        <li v-for="(item, index) in accounts" :key="keyOf(item)" :style="{ '--i': Math.min(index, 8) }" :data-account-suggestion="keyOf(item)">
          <div class="contact-duplicates-pair">
            <span><strong>{{ item.accountName }}</strong><small>{{ accountLine(item) }}</small></span>
            <AtlasIcon name="link" :size="15" />
            <span><strong>{{ item.contact.name }}</strong><small>{{ lineOf(item.contact.contact) || t.duplicates.contact }}</small></span>
          </div>
          <span class="contact-duplicates-reason">{{ evidenceOf(item) }}<template v-if="item.ownContactId"> · {{ t.duplicates.ownMerge }}</template></span>
          <Button variant="outline" size="sm" type="button" :autofocus="index === 0" :disabled="!!deciding" :aria-busy="deciding === keyOf(item)" data-account-link @click="decide(item, 'link')">
            <AtlasIcon name="link" :size="15" />{{ t.duplicates.link }}
          </Button>
          <Button variant="ghost" size="sm" type="button" :disabled="!!deciding" data-account-dismiss @click="decide(item, 'dismiss')">{{ t.duplicates.dismiss }}</Button>
          <RowActionsMenu :items="accountActions" :label="t.duplicates.actionsOf(item.accountName)" @select="decide(item, $event)" />
        </li>
      </TransitionGroup>
    </section>
    <h4 v-if="!error && accounts.length && pairs.length" class="contact-duplicates-subtitle">{{ t.duplicates.pairsTitle }}</h4>
    <TransitionGroup v-if="!error && pairs.length" tag="ul" name="contact-row" class="contact-duplicates-items" :aria-label="t.duplicates.title">
      <li v-for="(pair, index) in pairs" :key="`${pair.a.contactId}:${pair.b.contactId}`" :style="{ '--i': Math.min(index, 8) }" data-contact-pair>
        <div class="contact-duplicates-pair">
          <span><strong>{{ pair.a.name }}</strong><small v-if="lineOf(pair.a.contact)">{{ lineOf(pair.a.contact) }}</small></span>
          <AtlasIcon name="users" :size="15" />
          <span><strong>{{ pair.b.name }}</strong><small v-if="lineOf(pair.b.contact)">{{ lineOf(pair.b.contact) }}</small></span>
        </div>
        <span class="contact-duplicates-reason">{{ reasonOf(pair) }}</span>
        <Button variant="outline" size="sm" type="button" :autofocus="index === 0 && !accounts.length" data-contact-merge @click="ask(pair)">{{ t.duplicates.merge }}</Button>
      </li>
    </TransitionGroup>
    <ConfirmDialog :open="!!merging" :title="t.duplicates.mergeTitle" :confirm-label="t.duplicates.merge" confirm-icon="git-merge" :busy="busy" :error="mergeError || overCapacity || ''" :confirm-disabled="!!overCapacity" @confirm="merge" @cancel="merging = null">
      <template v-if="merging">
        <OptionPills v-model="kept" :label="t.duplicates.kept" :options="[{ value: 'a', label: pillOf(merging.a, t.duplicates.first), icon: 'user' }, { value: 'b', label: pillOf(merging.b, t.duplicates.second), icon: 'user' }]" data-contact-merge-kept />
        <template v-if="!overCapacity">
          <p>{{ t.duplicates.mergeText }}</p>
          <SummaryList :items="result" :label="t.duplicates.result" data-contact-merge-result />
        </template>
      </template>
    </ConfirmDialog>
  </section>
</template>

<style scoped>
.contact-duplicates { display: grid; gap: 14px; align-content: start; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.contact-duplicates-header { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 10px; }
.contact-duplicates-header h3 { margin: 0; font-size: 16px; }
.contact-duplicates-group { display: grid; gap: 8px; }
.contact-duplicates-group h4, .contact-duplicates-subtitle { margin: 0; font-size: 14px; }
.contact-duplicates-help { margin: 0; color: var(--muted); font-size: 12.5px; }
.contact-duplicates-items { position: relative; margin: 0; padding: 0; list-style: none; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); overflow: hidden; }
.contact-duplicates-items > li { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 14px; padding: 12px 12px 12px 16px; }
.contact-duplicates-items > li + li { border-top: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface)); }
.contact-duplicates-pair { display: flex; flex: 1 1 320px; flex-wrap: wrap; align-items: center; gap: 6px 12px; min-width: 0; color: var(--muted); }
.contact-duplicates-pair > span { display: grid; min-width: 0; color: var(--text); }
.contact-duplicates-pair small, .contact-duplicates-reason { color: var(--muted); font-size: 12.5px; }
.contact-row-enter-active { animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards; animation-delay: calc(var(--i, 0) * var(--st-stagger)); }
.contact-row-leave-active { position: absolute; animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-in) both; pointer-events: none; }
.contact-row-move { transition: transform var(--atlas-motion-base) var(--atlas-ease-inout); }
</style>
