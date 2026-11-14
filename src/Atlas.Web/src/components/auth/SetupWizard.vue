<script setup lang="ts">
import { ref } from "vue";
import { useHeightEase } from "../../composables/useHeightEase";
import { disclosureHooks } from "../settings/disclosure";
import { Button } from "../ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "../ui/tooltip";
import { Input } from "../ui/input";
import AtlasIcon from "../AtlasIcon.vue";
import AtlasBrandLogo from "../AtlasBrandLogo.vue";
import { Label } from "../ui/label";
import type { SetupStatus } from "../../api";
import t from "../../i18n/components-auth";
interface SetupForm {
  workspaceName: string;
  displayName: string;
  username: string;
  password: string;
  bootstrapToken: string;
}
const props = defineProps<{
  status: SetupStatus;
  form: SetupForm;
  step: number;
  pending: boolean;
  error: string;
}>();
const emit = defineEmits<{ "update:step": [step: number]; complete: [] }>();
const heading = ref<HTMLElement>();
const stage = ref<HTMLElement>();
const copied = ref(false),
  copyError = ref(false);
// The heading and the form of a step leave together and the next ones rise in; the stage between them eases from the
// height of the step that left to the one that arrives, so the card (and the card's place on the screen) does not jump.
// Focus follows the new heading once it is there.
const { rememberHeight, easeHeight } = useHeightEase(stage);
function focusHeading() { heading.value?.focus(); }
async function copyPath() {
  if (!props.status.tokenSource?.filePath) return;
  try {
    await navigator.clipboard.writeText(props.status.tokenSource.filePath);
    copied.value = true;
    copyError.value = false;
  } catch {
    copyError.value = true;
  }
}
</script>

<template>
  <section class="setup-wizard" aria-labelledby="entry-title">
    <aside class="sw-overview">
      <div class="sw-brand">
        <span><AtlasBrandLogo motion="hero" /></span>Atlas
      </div>
      <div class="sw-intro">
        <p class="sw-kicker">{{ t.setup.kicker }}</p>
        <h2>{{ t.setup.title1 }}<br />{{ t.setup.title2 }}</h2>
        <p>
          {{ t.setup.text1 }}<br />{{ t.setup.text2 }}
        </p>
      </div>
      <ol class="sw-steps" :aria-label="t.setup.progress">
        <li
          :aria-current="step === 1 ? 'step' : undefined"
          :class="{ done: step > 1 }"
        >
          <span class="sw-step-number"
            ><AtlasIcon v-if="step > 1" name="check" :size="16" /><template
              v-else
              >1</template
            ></span
          >
          <div>
            <strong>{{ t.setup.stepWiki }}</strong><small>{{ t.setup.stepWikiHint }}</small>
          </div>
        </li>
        <li :aria-current="step === 2 ? 'step' : undefined">
          <span class="sw-step-number">2</span>
          <div>
            <strong>{{ t.setup.stepAdmin }}</strong
            ><small>{{ t.setup.stepAdminHint }}</small>
          </div>
        </li>
      </ol>
      <div class="sw-connected">
        <AtlasIcon name="database" :size="15" /><span
          >{{ t.setup.dbConnected }}</span
        ><AtlasIcon name="check" :size="14" />
      </div>
    </aside>
    <div class="sw-content">
      <div ref="stage" class="sw-stage">
      <Transition name="sw-step" mode="out-in" @before-leave="rememberHeight" @enter="easeHeight" @after-enter="focusHeading">
      <div :key="step" class="sw-step">
      <header class="sw-heading">
        <span class="sw-counter">{{ t.setup.counter(step) }}</span>
        <h1 id="entry-title" ref="heading" tabindex="-1">
          {{
            step === 1
              ? t.setup.wikiTitle
              : t.setup.adminTitle
          }}
        </h1>
        <p>
          {{
            step === 1
              ? t.setup.wikiLead
              : t.setup.adminLead
          }}
        </p>
      </header>
      <form
        v-if="step === 1"
        class="sw-form"
        @submit.prevent="emit('update:step', 2)"
      >
        <fieldset :disabled="pending">
          <Label for="sw-name">{{ t.setup.wikiName }}</Label
          ><Input
            id="sw-name"
            v-model="form.workspaceName"
            required
            maxlength="100"
            autocomplete="organization"
            :placeholder="t.setup.wikiNamePlaceholder"
          />
          <p class="sw-hint">{{ t.setup.wikiNameHint }}</p>
        </fieldset>
        <div class="sw-preview" aria-hidden="true">
          <div class="sw-preview-icon"><AtlasBrandLogo motion="static" /></div>
          <div>
            <strong>{{
              form.workspaceName.trim() || t.setup.wikiFallback
            }}</strong
            ><span>{{ t.setup.preview }}</span>
          </div>
        </div>
        <footer class="sw-actions">
          <span>{{ t.setup.quick }}</span
          ><Button variant="default" size="lg" :disabled="pending">
            {{ t.setup.next }} <AtlasIcon name="chevron-right" :size="17" />
          </Button>
        </footer>
      </form>
      <form v-else class="sw-form" @submit.prevent="emit('complete')">
        <fieldset :disabled="pending">
          <div class="sw-fields">
            <div>
              <Label for="sw-display-name">{{ t.setup.yourName }}</Label
              ><Input
                id="sw-display-name"
                v-model="form.displayName"
                required
                maxlength="100"
                autocomplete="name"
                :placeholder="t.setup.namePlaceholder"
              />
            </div>
            <div>
              <Label for="sw-username">{{ t.setup.username }}</Label
              ><Input
                id="sw-username"
                v-model="form.username"
                required
                maxlength="100"
                autocomplete="username"
                :placeholder="t.setup.usernamePlaceholder"
              />
            </div>
          </div>
          <Label for="sw-password"
            >{{ t.setup.password }}
            <span
              >{{ t.setup.minLength(status.passwordMinimumLength ?? 12) }}</span
            ></Label
          ><Input
            id="sw-password"
            v-model="form.password"
            required
            :minlength="status.passwordMinimumLength ?? 12"
            maxlength="128"
            type="password"
            autocomplete="new-password"
          />
          <section
            v-if="status.bootstrapTokenRequired"
            class="sw-token"
            aria-labelledby="sw-token-title"
          >
            <div class="sw-token-heading">
              <AtlasIcon name="fingerprint" :size="19" />
              <h2 id="sw-token-title">{{ t.setup.confirmTitle }}</h2>
            </div>
            <p>{{ t.setup.confirmText }}</p>
            <Label for="sw-token">{{ t.setup.token }}</Label
            ><Input
              id="sw-token"
              v-model="form.bootstrapToken"
              required
              type="password"
              autocomplete="off"
              spellcheck="false"
              :placeholder="t.setup.tokenPlaceholder"
            />
            <details class="sw-token-help">
            <summary>{{ t.setup.whereToken }}</summary>
            <div class="sw-token-guide">
              <AtlasIcon name="square-code" :size="15" />
              <p>
                <strong>{{ t.setup.whereTokenTitle }}</strong
                  ><span>{{ t.setup.whereTokenText }}</span>
              </p>
            </div>
            <template v-if="status.tokenSource?.filePath"
              ><p class="sw-file-label">
                {{ t.setup.orFile }}
              </p>
              <div class="sw-file">
                <code
                  class="recovery-path"
                  tabindex="0"
                  :aria-label="t.setup.pathLabel"
                  >{{ status.tokenSource.filePath }}</code
                ><Tooltip><TooltipTrigger as-child><button
                  type="button"
                  :aria-label="
                    copied ? t.setup.pathCopied : t.setup.copyPathToFile
                  "
                  @click="copyPath"
                >
                  <AtlasIcon
                    v-if="copied"
                    name="check"
                    :size="17"
                  /><AtlasIcon v-else name="copy" :size="17" />
                </button></TooltipTrigger><TooltipContent>{{ copied ? t.setup.pathCopied : t.setup.copyPath }}</TooltipContent></Tooltip>
              </div>
              <span class="sw-copy-status" role="status">{{
                copyError
                  ? t.setup.copyManually
                  : copied
                    ? t.setup.pathCopiedDone
                    : ""
              }}</span></template
            >
            <p v-else class="sw-hint">
              {{
                status.tokenSource?.instructions ||
                t.setup.ownSetup
              }}
            </p>
            </details>
          </section>
        </fieldset>
        <p class="sw-permissions">
          {{ t.setup.permissions }}
        </p>
        <Transition :css="false" v-bind="disclosureHooks"><p v-if="error" class="notice error" role="alert">{{ error }}</p></Transition>
        <footer class="sw-actions">
          <Button
            variant="outline"
            size="lg"
            type="button"
            :disabled="pending"
            @click="emit('update:step', 1)"
          >
            {{ t.setup.back }}</Button
          ><Button variant="default" size="lg" :disabled="pending">
            {{ pending ? t.setup.preparing : t.setup.create
            }}<AtlasIcon v-if="!pending" name="chevron-right" :size="17" />
          </Button>
        </footer>
      </form>
      </div>
      </Transition>
      </div>
    </div>
  </section>
</template>

<style scoped>
.setup-wizard {
  display: grid;
  grid-template-columns: 270px minmax(0, 1fr);
  width: min(940px, 100%);
  color: var(--text);
  background: var(--surface);
  border: 1px solid var(--line);
  border-radius: 24px;
  box-shadow: 0 1px 2px rgb(20 47 59 / 0.05), 0 24px 80px rgb(20 47 59 / 0.1);
  overflow: hidden;
  animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) both;
}
/* The overview is the wiki's navigation in miniature: the accent's own light gradient, in dark its glow. */
.sw-overview {
  display: flex;
  flex-direction: column;
  padding: 32px 28px;
  background: var(--sidebar-fill, var(--sidebar));
  border-right: 1px solid var(--line);
}
.sw-brand {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 21px;
  font-weight: 750;
  letter-spacing: -0.5px;
}
.sw-brand > span {
  width: 60px;
  height: 60px;
  display: grid;
  place-items: center;
}
.sw-intro {
  margin: 46px 0 34px;
}
.sw-kicker {
  font-size: 10px;
  letter-spacing: 0.11em;
  font-weight: 650;
  color: var(--muted);
}
.sw-intro h2 {
  font-size: 25px;
  line-height: 1.28;
  letter-spacing: -0.7px;
  font-weight: 650;
  margin: 14px 0;
}
.sw-intro > p:last-child {
  font-size: 12px;
  line-height: 1.7;
  color: var(--muted);
}
.sw-steps {
  list-style: none;
  padding: 0;
  margin: 0 0 32px;
  display: grid;
  gap: 24px;
}
.sw-steps li {
  display: flex;
  gap: 12px;
  align-items: center;
  color: var(--muted);
  transition: color var(--atlas-motion-base) var(--atlas-ease-out);
}
.sw-step-number {
  width: 29px;
  height: 29px;
  display: grid;
  place-items: center;
  border: 1px solid var(--input-line);
  border-radius: 50%;
  font-size: 12px;
  font-weight: 650;
  flex: none;
  transition: background-color var(--atlas-motion-base) var(--atlas-ease-out), border-color var(--atlas-motion-base) var(--atlas-ease-out), color var(--atlas-motion-base) var(--atlas-ease-out), box-shadow var(--atlas-motion-base) var(--atlas-ease-out);
}
.sw-steps strong {
  display: block;
  font-size: 13px;
  font-weight: 600;
}
.sw-steps small {
  display: block;
  font-size: 11px;
  margin-top: 2px;
}
.sw-steps [aria-current] {
  color: var(--text);
}
.sw-steps [aria-current] .sw-step-number {
  background: var(--action, var(--accent));
  border-color: var(--action, var(--accent));
  color: var(--on-action, var(--on-accent));
  box-shadow: 0 0 0 4px color-mix(in srgb, var(--accent) 16%, transparent);
}
.sw-steps .done .sw-step-number {
  background: var(--success-bg);
  border-color: color-mix(in srgb, var(--success) 35%, var(--success-bg));
  color: var(--success);
}
/* A finished step turns into a tick that pops in (st-pop; reduced motion only fades it). */
.sw-steps .done .sw-step-number > svg { animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }
.sw-connected {
  margin-top: auto;
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--success);
  font-size: 11px;
  padding-top: 26px;
}
.sw-connected > svg:last-child {
  margin-left: auto;
}
.sw-content {
  min-width: 0;
  padding: 36px 38px 28px;
}
.sw-counter {
  color: var(--accent);
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.12em;
}
.sw-heading h1 {
  font-size: 27px;
  font-weight: 650;
  line-height: 1.23;
  letter-spacing: -0.8px;
  margin: 10px 0;
  max-width: 430px;
  outline: none;
}
.sw-heading > p {
  color: var(--muted);
  font-size: 13px;
  margin: 0 0 25px;
}
.setup-wizard .sw-content .sw-form {
  margin: 0;
}
/* A flow root: the margins of the step stay inside the stage, so its height is the whole step. The two steps swap like the
   steps of a settings flow (.st-step-*): leave fast, rise in; reduced motion keeps only the fade. */
.sw-stage { display: flow-root; }
.sw-step-enter-active { animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) both; }
.sw-step-leave-active { animation: st-rise-out var(--atlas-motion-fast) var(--atlas-ease-in) both; pointer-events: none; }
.sw-token-help { margin-top: 12px; font-size: 12px; }
.sw-token-help summary { color: var(--accent); cursor: pointer; }
.sw-token-help summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 4px; border-radius: 4px; }
.setup-wizard .sw-content .sw-form fieldset {
  padding: 0;
  margin: 0;
  border: 0;
  min-width: 0;
}
.setup-wizard .sw-content .sw-form label {
  display: flex;
  flex-direction: row;
  align-items: center;
  justify-content: space-between;
  font-size: 12px;
  margin: 0 0 7px;
  font-weight: 600;
  gap: 8px;
}
.setup-wizard .sw-content .sw-form label > span {
  font-size: 11px;
  font-weight: 400;
  color: var(--muted);
}
.sw-hint {
  color: var(--muted);
  font-size: 11px;
  margin: 8px 0 0;
}
.sw-fields {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 14px;
  margin-bottom: 18px;
}
.sw-fields > div {
  min-width: 0;
}
.sw-preview {
  display: flex;
  align-items: center;
  gap: 13px;
  border: 1px solid var(--line);
  border-radius: 12px;
  padding: 18px;
  margin: 30px 0 42px;
  background: var(--sidebar);
}
.sw-preview-icon {
  display: grid;
  place-items: center;
  width: 42px;
  height: 42px;
  flex: none;
}
.sw-preview strong {
  display: block;
  font-size: 14px;
  overflow-wrap: anywhere;
}
.sw-preview strong + span {
  display: block;
  font-size: 11px;
  color: var(--muted);
  margin-top: 3px;
}
.sw-token {
  margin-top: 22px;
  border-top: 1px solid var(--line);
  padding-top: 19px;
}
.sw-token-heading {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--accent);
}
.sw-token-heading h2 {
  font-size: 14px;
  font-weight: 650;
  color: var(--text);
  margin: 0;
}
.sw-token > p:first-of-type {
  color: var(--muted);
  font-size: 12px;
  margin: 7px 0 15px;
}
.sw-token-guide {
  display: flex;
  align-items: flex-start;
  gap: 9px;
  margin-top: 14px;
  color: var(--muted);
}
.sw-token-guide > svg {
  margin-top: 3px;
  flex: none;
}
.sw-token-guide p {
  margin: 0;
  font-size: 11px;
}
.sw-token-guide strong {
  display: block;
  color: var(--text);
  font-weight: 600;
  font-size: 11px;
  margin-bottom: 2px;
}
.sw-token-guide span {
  display: block;
}
.sw-token-guide b {
  font-weight: 600;
}
.sw-file-label {
  font-size: 10px;
  color: var(--muted);
  margin: 12px 0 6px;
}
.sw-file {
  display: flex;
  min-width: 0;
  align-items: center;
  border: 1px solid var(--line);
  border-radius: 7px;
  background: var(--sidebar);
  padding: 0 5px 0 10px;
}
.sw-file .recovery-path {
  display: block;
  margin: 0;
  flex: 1;
  min-width: 0;
  overflow-x: auto;
  white-space: nowrap;
  overflow-wrap: normal;
  padding: 11px 0;
  font-size: 10px;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  scrollbar-width: thin;
  line-height: 1.5;
}
.setup-wizard .sw-file button {
  border: 0;
  background: transparent;
  color: var(--muted);
  padding: 8px;
  margin-left: 5px;
  display: grid;
  place-items: center;
  flex: none;
}
.sw-copy-status {
  display: block;
  min-height: 17px;
  margin-top: 3px;
  font-size: 10px;
  color: var(--muted);
}
.sw-permissions {
  font-size: 11px;
  color: var(--muted);
  line-height: 1.6;
  margin: 10px 0 20px;
}
.sw-actions {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
  margin-top: 20px;
}
.sw-actions > span {
  color: var(--muted);
  font-size: 11px;
}
/* The buttons are the shared Button (default and outline, size lg): the wizard no longer restyles it. Only the arrow
   of the primary one nudges on hover, a decorative child. */
.setup-wizard .sw-actions [data-variant="default"] > svg { transition: transform var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) {
  .setup-wizard .sw-actions [data-variant="default"]:hover:not(:disabled) > svg { transform: translateX(var(--atlas-lift)); }
}
:global(html[data-mode="dark"] .atlas .setup-wizard) { box-shadow: var(--shadow-modal), var(--atlas-glow-ring); }
:global(html[data-mode="dark"] .atlas .sw-overview) {
  background: var(--atlas-glow, none), var(--sidebar);
}
@media (max-width: 740px) {
  .setup-wizard {
    grid-template-columns: 1fr;
    max-width: 560px;
    border-radius: 18px;
  }
  .sw-overview {
    padding: 22px 24px 18px;
    border-right: 0;
    border-bottom: 1px solid var(--line);
  }
  .sw-brand {
    font-size: 19px;
  }
  .sw-brand > span {
    width: 38px;
    height: 38px;
  }
  .sw-intro,
  .sw-connected {
    display: none;
  }
  .sw-steps {
    grid-template-columns: 1fr 1fr;
    gap: 15px;
    margin: 22px 0 0;
  }
  .sw-steps small {
    display: none;
  }
  .sw-steps strong {
    font-size: 12px;
  }
  .sw-steps li {
    gap: 8px;
  }
  .sw-step-number {
    width: 26px;
    height: 26px;
  }
  .sw-content {
    padding: 25px 24px 24px;
  }
  .sw-heading h1 {
    font-size: 25px;
  }
}
@media (max-width: 420px) {
  .sw-content {
    padding: 23px 20px;
  }
  .sw-overview {
    padding: 20px;
  }
  .sw-fields {
    grid-template-columns: 1fr;
    gap: 16px;
  }
  .sw-heading h1 {
    font-size: 24px;
  }
  .sw-heading > p {
    font-size: 12px;
  }
  .sw-actions > span {
    max-width: 90px;
  }
  .sw-preview {
    padding: 14px;
  }
}
</style>
