<script lang="ts">
import { api, type User } from "../../../api";
import { adminApi, type DirectoryUser, type Invitation } from "../../../administration";
import { accessApi } from "../../../accessApi";
import type { PrincipalAccess } from "../access/accessModel";
import type { InviteDraft } from "./types";

/** What the part reads and writes; the gallery and tests pass their own. */
export interface PersonAccountSource {
  me: () => Promise<Pick<User, "id" | "isWikiAdmin">>;
  account: (id: string) => Promise<DirectoryUser | undefined>;
  principal: (id: string) => Promise<PrincipalAccess>;
  create: (draft: InviteDraft & { contactId: string }, proof?: string | null) => Promise<Invitation>;
}
const liveSource: PersonAccountSource = {
  me: () => api<User>("/auth/me"),
  account: id => adminApi.users({ id }).then(users => users[0]),
  principal: id => accessApi.principal("user", id),
  create: (draft, proof) => adminApi.createUser(draft, proof),
};
</script>

<script setup lang="ts">
import { locale } from "../../../i18n";
import t from "../../../i18n/settings-people";
import { computed, onBeforeUnmount, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import AppDialog from "../../AppDialog.vue";
import { Button } from "../../ui/button";
import { disclosureHooks } from "../disclosure";
import { errorMessage } from "../../../api";
import { formatDateTime } from "../../../formatDate";
import AccessLevelBadge from "../AccessLevelBadge.vue";
import EmptyState from "../EmptyState.vue";
import LoadingNote from "../LoadingNote.vue";
import StatusBadge from "../StatusBadge.vue";
import { allLevelOptions } from "../access/accessKinds";
import { useOptionalSettingsRoute } from "../useSettingsRoute";
import AccountStatusBadge from "./AccountStatusBadge.vue";
import InviteAccountWizard from "./InviteAccountWizard.vue";
import { accountSourceLabels } from "./accountStatus";

/**
 * Účet, the wiki administrator's part of a person's profile (ContentDetail): the account of the contact (`accountId`, its directoryId)
 * with its state, groups and what spaces it reaches and how, and the way into its full card (Přístupy, Účet, Skupiny, Tokeny, Činnost)
 * and to Přidat přístup; read only, every change happens on the card. A contact without an account offers „Vytvořit účet“: the invitation
 * of Lidé (InviteAccountWizard) with the contact's name, the new account linked to this contact in the same request. Anyone but a wiki
 * administrator sees nothing and the admin endpoints are not asked (they refuse anyone else anyway). It replaced PersonPeek, the
 * separate dock of Lidé: Lidé opens the person's profile.
 */
const props = withDefaults(defineProps<{ accountId?: string; contact: { id: string; name: string }; spaces?: { id: string; color: string }[];
  source?: PersonAccountSource }>(), { accountId: undefined, spaces: undefined, source: () => liveSource });
const emit = defineEmits<{ created: [invitation: Invitation] }>();
const router = useOptionalSettingsRoute();
const href = (path: string) => (router ? router.href(path) : `#/settings/${path}`);
const me = ref<Pick<User, "id" | "isWikiAdmin">>();
const account = ref<DirectoryUser>();
const access = ref<PrincipalAccess>();
const error = ref("");
const creating = ref(false);
let request = 0;
async function load() {
  const current = ++request;
  account.value = undefined; access.value = undefined; error.value = "";
  try {
    me.value ??= await props.source.me();
    if (current !== request || !me.value.isWikiAdmin || !props.accountId) return;
    account.value = await props.source.account(props.accountId);
    const next = await props.source.principal(props.accountId);
    if (current === request) access.value = next;
  } catch (failure) {
    if (current === request) error.value = errorMessage(failure);
  }
}
watch(() => props.accountId, load, { immediate: true });
onBeforeUnmount(() => request++);
const create = (draft: InviteDraft, proof?: string | null) => props.source.create({ ...draft, contactId: props.contact.id }, proof);
function created(invitation: Invitation) {
  creating.value = false;
  emit("created", invitation);
}

const path = computed(() => `people/accounts/${props.accountId}`);
const groups = computed(() => [...(access.value?.principal.groups ?? [])].sort((a, b) => a.name.localeCompare(b.name, locale)));
const spaceEntries = computed(() => (access.value?.entries ?? []).filter((entry) => entry.kind === "space").sort((a, b) => a.title.localeCompare(b.title, locale)));
const otherEntries = computed(() => (access.value?.entries.length ?? 0) - spaceEntries.value.length);
const spaceColor = (id?: string | null) => props.spaces?.find((space) => space.id === id)?.color ?? "blue";
const spaceOptions = allLevelOptions();
// The loading note keeps `min-height: 20px`, which would hold its box open for the whole fold and drop the card by its height at the end.
const loadingHooks = {
  ...disclosureHooks,
  onLeave(element: Element, done: () => void) {
    (element as HTMLElement).style.minHeight = "0";
    disclosureHooks.onLeave(element, done);
  },
};
</script>

<template>
  <section v-if="me?.isWikiAdmin" class="person-peek" :aria-label="t.peek.title" data-person-account>
    <h4 class="st-group-label">{{ t.peek.title }}</h4>
    <template v-if="!accountId">
      <p class="person-peek-empty">{{ t.peek.noAccount }}</p>
      <div class="person-peek-actions">
        <Button size="sm" variant="outline" type="button" data-person-account-create @click="creating = true"><AtlasIcon name="plus" :size="15" />{{ t.peek.createAccount }}</Button>
      </div>
      <AppDialog :open="creating" :title="t.peek.createAccountOf(contact.name)" @close="creating = false">
        <InviteAccountWizard v-if="creating" :submit="create" :initial="{ displayName: contact.name }" @done="created" @cancel="creating = false" />
      </AppDialog>
    </template>
    <template v-else>
      <template v-if="account">
        <p class="person-peek-meta">{{ account.username }} · {{ accountSourceLabels[account.source].label.toLocaleLowerCase(locale) }}</p>
        <div class="person-peek-badges">
          <AccountStatusBadge :account="account" size="sm" />
          <StatusBadge v-if="account.isWikiAdmin" tone="accent" icon="shield-check" :label="t.common.wikiAdmin" size="sm" />
          <StatusBadge v-if="account.id === me.id" tone="info" icon="account" :label="t.peek.you" size="sm" />
        </div>
      </template>
      <div class="person-peek-actions">
        <Button size="sm" as-child><a :href="href(path)" data-person-account-open>{{ t.peek.openCard }}</a></Button>
        <Button size="sm" variant="outline" as-child><a :href="href(`${path}/new`)"><AtlasIcon name="plus" :size="15" />{{ t.common.addAccess }}</a></Button>
        <Button size="sm" variant="ghost" as-child><a :href="href(`${path}/account`)">{{ t.peek.accountAndPassword }}</a></Button>
      </div>

      <!-- What loads last unfolds under the header (height and opacity) instead of making the card grow in one frame. -->
      <Transition :css="false" v-bind="loadingHooks"><LoadingNote v-if="!access && !error" :label="t.common.loadingAccess" /></Transition>
      <p v-if="error" class="st-note" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p>
      <Transition :css="false" v-bind="disclosureHooks">
        <div v-if="access" class="person-peek-body">
          <p v-if="access.principal.lastSignInAt" class="person-peek-meta">{{ t.peek.lastSignIn(formatDateTime(access.principal.lastSignInAt)) }}</p>
          <section class="person-peek-group" aria-labelledby="person-peek-groups">
            <h4 id="person-peek-groups" class="st-group-label">{{ t.common.groups }}</h4>
            <ul v-if="groups.length" class="person-peek-chips">
              <li v-for="group in groups" :key="group.id"><a :href="href(`people/groups/${group.id}`)">{{ group.name }}</a></li>
            </ul>
            <EmptyState v-else inline compact icon="users" :title="t.peek.noGroups" />
          </section>
          <section class="person-peek-group" aria-labelledby="person-peek-spaces">
            <h4 id="person-peek-spaces" class="st-group-label">{{ t.peek.spaceAccess }}</h4>
            <ul v-if="spaceEntries.length" class="person-peek-spaces">
              <li v-for="entry in spaceEntries" :key="entry.resourceId">
                <span class="person-peek-dot" :data-color="spaceColor(entry.resourceId)" aria-hidden="true" />
                <span class="person-peek-space" :title="entry.title">{{ entry.title }}</span>
                <span class="person-peek-via" :title="entry.sources[0]?.via === 'direct' ? undefined : entry.sources[0]?.label">{{ entry.sources[0]?.via === "direct" ? t.peek.directly : entry.sources[0]?.label }}</span>
                <AccessLevelBadge v-if="entry.level" :options="spaceOptions" :value="entry.level" tone="level" />
                <span v-else class="person-peek-via">{{ t.peek.noAccess }}</span>
              </li>
            </ul>
            <EmptyState v-else inline compact icon="book" :title="t.peek.noSpaces" />
            <a v-if="otherEntries > 0" class="st-link person-peek-more" :href="href(path)">{{ t.peek.more(otherEntries) }}<AtlasIcon name="arrow-right" :size="14" /></a>
          </section>
        </div>
      </Transition>
    </template>
  </section>
</template>

<style scoped>
.person-peek > * { flex-shrink: 0; min-width: 0; }
/* A column and not a grid: a note that unfolds under the header (disclosureHooks) takes its gap with it, a grid track cannot. */
.person-peek { display: flex; flex-direction: column; gap: 10px; min-width: 0; padding-top: 12px; border-top: 1px solid var(--st-line-soft, var(--line)); }
.person-peek > h4 { margin: 0; }
.person-peek-body { display: flex; flex-direction: column; gap: 14px; }
.person-peek-badges, .person-peek-actions { display: flex; flex-wrap: wrap; gap: 6px; }
/* Links drawn as the settings' buttons: a button, never an underlined link (the panel holds no settings-ui link rules). */
.person-peek-actions a { text-decoration: none; }
.person-peek-meta { margin: 0; color: var(--muted); font-size: 12.5px; }
.person-peek-group { display: grid; grid-template-columns: minmax(0, 1fr); gap: 8px; padding-top: 12px; border-top: 1px solid var(--st-line-soft); }
.person-peek-chips { display: flex; flex-wrap: wrap; gap: 6px; margin: 0; padding: 0; list-style: none; }
.person-peek-chips a { display: inline-flex; align-items: center; min-height: 28px; padding: 0 9px; border: 1px solid var(--line); border-radius: 8px; color: var(--text); font-size: 13px; text-decoration: none; transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) {
  .person-peek-chips a:hover { border-color: var(--accent); color: var(--accent); }
}
.person-peek-chips a:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.person-peek-spaces { display: grid; grid-template-columns: minmax(0, 1fr); margin: 0; padding: 0; list-style: none; }
.person-peek-spaces > li { display: flex; align-items: center; gap: 8px; min-height: 40px; border-top: 1px solid var(--st-line-soft); }
.person-peek-spaces > li:first-child { border-top: 0; }
.person-peek-dot { flex-shrink: 0; width: 10px; height: 10px; border-radius: 3px; background: var(--appearance-fg, var(--accent)); }
.person-peek-space { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 13.5px; }
.person-peek-via { color: var(--muted); font-size: 12px; white-space: nowrap; max-width: 110px; overflow: hidden; text-overflow: ellipsis; }
.person-peek-empty { margin: 0; color: var(--muted); font-size: 13px; }
.person-peek-more { font-size: 13px; }
</style>
