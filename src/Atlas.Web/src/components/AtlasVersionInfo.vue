<script setup lang="ts">
import { computed, ref, useId } from "vue";
import AtlasIcon from "./AtlasIcon.vue";
import t from "../i18n/components-a";
import { disclosureHooks } from "./settings/disclosure";

// `href`: where the release name leads (Co je nového) for a signed-in account; the arrow alone then unfolds the details.
const props = defineProps<{ version?: string; href?: string }>();
// The details are only hidden, never removed, so what a fold left on them (the leaving hook makes them inert and keeps
// the folded frame) must be cleared before they unfold again.
function reopen(element: Element) {
  element.removeAttribute("inert");
  element.removeAttribute("aria-hidden");
  for (const animation of element.getAnimations?.() ?? []) animation.cancel();
}
// The server answers "<release>+<commit>"; a local build may carry no commit.
const release = computed(() => props.version?.split("+")[0]);
const commit = computed(() => props.version?.split("+")[1]?.slice(0, 7));
const editor = __MARKDOWN_EDITOR_VERSION__;
const open = ref(false);
const details = useId();
</script>

<template>
  <div class="atlas-version">
    <a v-if="href" class="atlas-version-toggle atlas-version-link" :href="href" :title="t.versionInfo.whatsNew">Atlas{{ release ? ` ${release}` : "" }}</a>
    <button
      type="button"
      class="atlas-version-toggle"
      :aria-expanded="open"
      :aria-controls="details"
      :aria-label="href ? t.versionInfo.info : undefined"
      :title="t.versionInfo.info"
      @click="open = !open"
    >
      <template v-if="!href">Atlas{{ release ? ` ${release}` : "" }}</template><AtlasIcon name="down" :size="12" />
    </button>
    <Transition :css="false" v-bind="disclosureHooks" @before-enter="reopen">
      <div v-show="open" :id="details" class="atlas-version-details">
        <dl>
          <div>
            <dt>Atlas</dt>
            <dd>{{ release ?? t.versionInfo.unknown }}<template v-if="commit"> · {{ commit }}</template></dd>
          </div>
          <div>
            <dt>Markdown editor</dt>
            <dd>{{ editor }}</dd>
          </div>
        </dl>
        <slot />
      </div>
    </Transition>
  </div>
</template>

<style scoped>
.atlas-version {
  font-size: 11px;
  color: var(--muted);
}
.atlas-version-toggle {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 2px 4px;
  margin-left: -4px;
  border: 0;
  border-radius: 4px;
  background: none;
  color: inherit;
  font: inherit;
}
.atlas-version-link {
  margin-left: -4px;
  text-decoration: none;
}
.atlas-version-link + .atlas-version-toggle {
  margin-left: 0;
}
/* Muted at full strength: at .75 opacity the 11 px text had 3.4:1 on the light login card (every accent), under the 4.5:1 of
   small text. Pointed at or open it takes the text colour instead of more opacity. */
.atlas-version-toggle[aria-expanded="true"] {
  color: var(--text);
}
@media (hover: hover) and (pointer: fine) {
  .atlas-version-toggle:hover {
    color: var(--text);
  }
}
/* The arrow turns and the details unfold and fold with it (board 16 „Rozbalení“, disclosureHooks); both used to switch in one frame. */
.atlas-version-toggle :deep(svg) {
  transform: rotate(-90deg);
  transition: transform var(--atlas-motion-base) var(--atlas-ease-out);
}
.atlas-version-toggle[aria-expanded="true"] :deep(svg) {
  transform: rotate(0deg);
}
.atlas-version-details {
  margin-top: 6px;
}
.atlas-version-details dl {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 2px 12px;
  margin: 0;
}
.atlas-version-details dl > div {
  display: contents;
}
.atlas-version-details dd {
  margin: 0;
  font-variant-numeric: tabular-nums;
}
</style>
