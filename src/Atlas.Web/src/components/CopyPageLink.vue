<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch, type ComponentPublicInstance } from "vue";
import { fieldElement, hostElement } from "../lib/templateRef";
import { FEEDBACK_HOLD_MS } from "../motion/aiMotion";
import { Button } from "./ui/button";
import AtlasIcon from './AtlasIcon.vue';
import { pageLink } from "../pageLinks";
import { Popover, PopoverAnchor, PopoverContent } from "./ui/popover";
import { Tooltip, TooltipContent, TooltipTrigger } from "./ui/tooltip";
import { Input } from "./ui/input";
import t from "../i18n/components-a";
const props = withDefaults(defineProps<{ pageId?: string; href?: string; label?: string; text?: boolean }>(), { label: () => t.copyLink.label, text: false });
const copied = ref(false), manual = ref(false), busy = ref(false);
const field = ref<ComponentPublicInstance>(), button = ref<ComponentPublicInstance>();
let attempt = 0, returnFocus: Element | null = null;
let copiedTimer: ReturnType<typeof setTimeout> | undefined;
onBeforeUnmount(() => { attempt++; clearTimeout(copiedTimer); });
const url = computed(() => {
  return props.href ?? pageLink(props.pageId ?? "");
});
watch(() => [props.pageId, props.href], () => { attempt++; copied.value = false; manual.value = false; busy.value = false; clearTimeout(copiedTimer); });
async function copy() {
  if (busy.value) return;
  const current = ++attempt;
  busy.value = true; copied.value = false; manual.value = false;
  clearTimeout(copiedTimer);
  try {
    await navigator.clipboard.writeText(url.value);
    if (current === attempt) {
      copied.value = true;
      // A brief accent tint, not a persisted "done" state: the icon reverts to link
      // on its own so a second copy always looks like an action, not a toggle.
      copiedTimer = setTimeout(() => { if (current === attempt) copied.value = false; }, FEEDBACK_HOLD_MS);
    }
  } catch {
    if (current !== attempt) return;
    returnFocus = document.activeElement;
    manual.value = true;
  } finally { if (current === attempt) busy.value = false; }
}
function focusField() { const element = fieldElement(field.value); element?.focus(); element?.select(); }
function dismiss() {
  manual.value = false;
  const target = hostElement(button.value);
  target?.focus();
  // A phone hides the button (the page's actions sit in a menu there): back to where the copy was asked from.
  if (document.activeElement !== target && returnFocus instanceof HTMLElement && returnFocus.isConnected) returnFocus.focus();
}
defineExpose({ copy });
</script>
<template>
  <span class="page-link-control">
    <!-- An anchor rather than a trigger: the button copies, it does not toggle a
         surface. The popover is opened by the clipboard failing. -->
    <Popover :open="manual" @update:open="value => { if (!value) dismiss(); }">
      <PopoverAnchor as-child>
        <Tooltip><TooltipTrigger as-child><Button variant="ghost" :size="text ? 'sm' : 'icon-sm'" ref="button" type="button" class="text-muted-foreground" :class="copied ? 'text-primary' : undefined" :disabled="busy" :aria-label="label" @click="copy"><AtlasIcon :name="copied ? 'check' : 'link'" :size="text ? 14 : 18" /><span v-if="text">{{ label }}</span></Button></TooltipTrigger><TooltipContent>{{ label }}</TooltipContent></Tooltip>
      </PopoverAnchor>
      <PopoverContent class="flex w-max max-w-[min(380px,calc(100vw-2rem))] flex-wrap items-center gap-2" align="start" @open-auto-focus.prevent="focusField">
        <span role="status" class="w-full text-xs leading-normal text-muted-foreground">{{ t.copyLink.manual }}</span>
        <Input ref="field" class="w-[220px] flex-1" :model-value="url" readonly :aria-label="t.copyLink.field" @focus="fieldElement(field)?.select()" />
        <Button variant="outline" type="button" @click="dismiss">{{ t.copyLink.done }}</Button>
      </PopoverContent>
    </Popover>
    <span v-if="copied" class="visually-hidden" role="status" aria-live="polite">{{ t.copyLink.copied }}</span>
  </span>
</template>
<style scoped>
.page-link-control { position: relative; display: inline-flex; align-items: center; }
/* The check that confirms the copy pops in like the one of every other copy (CopyField, OneTimeSecret): the link icon
   changed into a check without any motion, the only copy confirmation that did not answer where the eye was. */
.page-link-control :deep(svg[data-icon="check"]) { animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }
/* Hidden by the shared .visually-hidden of shell.css; this status line also resets padding and margin. */
.visually-hidden { padding: 0; margin: -1px; }
/* Anchored under the trigger instead of widening the action row (finding 2.4).
   Placement is reka-ui's now -- including flipping away from a viewport edge,
   which the old fixed-position media query stood in for. */
</style>
