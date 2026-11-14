import { defineComponent, getCurrentInstance, getTransitionRawChildren, resolveTransitionHooks, setTransitionHooks, useTransitionState, type VNode } from "vue";
import { animateAtlas, holdFrame, motionMs, prefersReducedMotion } from "../../motion/aiMotion";

/**
 * Transition hooks that unfold a note the way InlineReveal unfolds a section (board 16: height and opacity, base to open,
 * fast to close), for a message that appears and goes away on its own: a field's error, a failure under a form, a hint.
 * `<Transition :css="false" v-bind="disclosureHooks"><p v-if="error">…</p></Transition>`. The element's own margin,
 * padding and, in a column of flex items, the gap above it grow with it, so nothing below jumps when it arrives or
 * leaves. A grid cannot give a gap back (a track never gets smaller than 0), so in a grid the space above the note
 * is its own margin, not the grid's gap.
 * Under reduced motion only the fade remains (animateAtlas), as everywhere. Where the whole section is the disclosure,
 * use InlineReveal; this is for the single element that a `v-if` shows.
 */
function opened(element: HTMLElement) {
  const style = getComputedStyle(element);
  const parent = element.parentElement ? getComputedStyle(element.parentElement) : undefined;
  const stacked = !!parent && parent.display.includes("flex") && parent.flexDirection.startsWith("column");
  const gap = stacked && element.previousElementSibling ? Number.parseFloat(parent!.rowGap) || 0 : 0;
  const open: Keyframe = {
    height: `${element.offsetHeight}px`, opacity: 1, overflow: "clip",
    marginTop: style.marginTop, marginBottom: style.marginBottom, paddingTop: style.paddingTop, paddingBottom: style.paddingBottom,
  };
  const closed: Keyframe = { height: "0px", opacity: 0, overflow: "clip", marginTop: `${-gap}px`, marginBottom: "0px", paddingTop: "0px", paddingBottom: "0px" };
  return { open, closed };
}

function whenDone(animation: Animation | null, done: () => void) {
  if (!animation) { done(); return; }
  animation.addEventListener("finish", done);
  animation.addEventListener("cancel", done);
}

export const disclosureHooks = {
  onEnter(element: Element, done: () => void) {
    const node = element as HTMLElement;
    const { open, closed } = opened(node);
    whenDone(animateAtlas(node, [closed, open], { duration: motionMs(node, "--atlas-motion-base"), fill: "none" }), done);
  },
  onLeave(element: Element, done: () => void) {
    const node = element as HTMLElement;
    const { open, closed } = opened(node);
    // A message that is leaving is a still picture: it must not be announced again or take focus.
    node.setAttribute("inert", "");
    node.setAttribute("aria-hidden", "true");
    whenDone(animateAtlas(node, [open, closed], { duration: motionMs(node, "--atlas-motion-fast"), fill: "forwards" }), done);
  },
};

/**
 * The hooks of one group of siblings. With motion they are disclosureHooks: what leaves folds while what joins unfolds, and
 * the parent eases from one height to the other. Reduced motion keeps the fade and drops the height, so a child that took
 * its room while others were leaving stood under them at full height until they were removed: a flow whose final step
 * replaced its form grew by the step and lost the form one frame later (1332 → 1646 → 401 px), and the step, three quarters
 * visible by then, jumped up by the form's height. Under reduced motion the group therefore takes turns, as the steps of a
 * StepFlow do: what leaves fades where it stands, and a child that joins meanwhile, or a closed one that opens (the next
 * step of a flow), is in the document at once, read and focusable, but held at no height until the last leaving child is
 * removed. It fades in from that frame, so the parent changes its height once, between the two fades.
 */
function takingTurns() {
  const leaving = new Set<Element>();
  const waiting = new Map<Element, { hold: Animation | null; done: () => void }>();
  function wait(element: Element, done: () => void) {
    // The closed end of the unfolding and the borders, which an unfolding keeps: the child takes no room at all.
    const { closed } = opened(element as HTMLElement);
    waiting.set(element, { hold: holdFrame(element, { ...closed, borderTopWidth: "0px", borderBottomWidth: "0px" }), done });
  }
  // Always from a microtask: after the patch that removed the last leaving child (a child that leaves in that same patch has
  // not had its turn and gets none) and before the frame, so the room is given and taken in one.
  function release() {
    if (leaving.size) return;
    for (const [node, { hold, done }] of waiting) { hold?.cancel(); disclosureHooks.onEnter(node, done); }
    waiting.clear();
  }
  function left(element: Element) {
    if (leaving.delete(element) && !leaving.size) queueMicrotask(release);
  }
  return {
    /**
     * Before the patch in which keyed children leave: the closed ones that stay are held now, because the patch opens them
     * before it reaches the ones that leave. It compares keys and reads the document only when a child does leave.
     */
    before(shown: VNode[], next: VNode[]) {
      const keys = new Set(next.map((child) => child.key));
      const first = shown.find((child) => child.key != null && !keys.has(child.key));
      if (!first || !prefersReducedMotion(first.el as Element | null)) return;
      for (const { key, el } of shown) if (key != null && keys.has(key) && el instanceof HTMLElement && !el.offsetHeight && !waiting.has(el)) wait(el, () => {});
      queueMicrotask(release);
    },
    hooks: {
      onEnter(element: Element, done: () => void) {
        if (leaving.size) wait(element, done);
        else disclosureHooks.onEnter(element, done);
      },
      onLeave(element: Element, done: () => void) {
        const unseen = waiting.get(element);
        if (unseen) { waiting.delete(element); done(); unseen.hold?.cancel(); return; }
        if (prefersReducedMotion(element)) leaving.add(element);
        disclosureHooks.onLeave(element, done);
      },
      onAfterLeave: left,
      onLeaveCancelled: left,
    },
  };
}

/**
 * The same hooks for keyed siblings that come and go on their own, such as the steps of a ProgressiveFlow: a child unfolds
 * when it joins and folds before it is removed. It renders no element of its own, so the children stay children of the
 * parent, with its grid and margins.
 * Use it where a `<TransitionGroup :css="false" v-bind="disclosureHooks">` without a tag would stand. Vue's group also
 * prepares a move transition on every render, whether or not anything joined or left: it reads the box of each child and
 * then clones the first one into the parent to read its computed style. A form renders on every keystroke and its
 * children never move, so this group does only the enter and leave half, with the building blocks Vue's own group uses.
 * Under reduced motion its children take turns (takingTurns).
 */
export const DisclosureGroup = defineComponent({
  name: "DisclosureGroup",
  setup(_, { slots }) {
    const instance = getCurrentInstance()!;
    const state = useTransitionState();
    const turns = takingTurns();
    return () => {
      const children = slots.default ? getTransitionRawChildren(slots.default()) : [];
      turns.before((instance.subTree?.children ?? []) as VNode[], children);
      for (const child of children) if (child.key != null) setTransitionHooks(child, resolveTransitionHooks(child, turns.hooks, state, instance));
      return children;
    };
  },
});
