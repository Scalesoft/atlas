import type { Editor } from "@tiptap/core";
import { markdownChangeMapping } from "@atlas/markdown/core";
import { Plugin, PluginKey, type EditorState } from "@tiptap/pm/state";
import type { Mapping } from "@tiptap/pm/transform";
import { Decoration, DecorationSet } from "@tiptap/pm/view";
import { ySyncPluginKey } from "@tiptap/y-tiptap";
import type * as Y from "yjs";
import { motionMs, prefersReducedMotion } from "../motion/aiMotion";

/**
 * An agent's write arrives as one update (LivePageWrites: written at once, under the page's gate, so people are never held up); each
 * editor types it in, as the promo film's AI writes: its text shows word by word at an even pace and the agent's caret rides the front.
 * The text is in the document all along (saving, search and a person's edits see it); its letters are only invisible until reached, so
 * nothing reflows. The agent writes as the Yjs client of its presence (ILiveWriteAuthority), so its update is known before it applies.
 * Under reduced motion the text is there at once.
 */
export interface AgentReveal {
  /** The awareness client ids of the agents present (their „ai:“ peers). */
  setAgents(clientIds: ReadonlySet<number>): void;
  /** Where an agent's caret stands while its text is typed in; undefined once it is all there. */
  front(clientId: number): number | undefined;
  destroy(): void;
}

interface Typing { ranges: readonly { from: number; to: number }[]; started: number; duration: number }
interface RevealState { typing: ReadonlyMap<number, Typing>; decorations: DecorationSet }

const key = new PluginKey<RevealState>("atlasAgentReveal");
const empty: RevealState = { typing: new Map(), decorations: DecorationSet.empty };

export function createAgentReveal(editor: Editor): AgentReveal {
  let agents: ReadonlySet<number> = new Set();
  // Set while Yjs applies an update of an agent's client: the editor's transaction that follows is that agent's write.
  let writing: number | undefined;
  let doc: Y.Doc | undefined;
  let frame = 0;
  let shownAt = new Map<number, number>();
  const beforeObservers = (transaction: Y.Transaction) => {
    writing = [...agents].find(client => (transaction.afterState.get(client) ?? 0) > (transaction.beforeState.get(client) ?? 0));
  };
  const afterTransaction = () => { writing = undefined; };
  const watch = (state: EditorState) => {
    const next = (ySyncPluginKey.getState(state) as { binding?: { doc: Y.Doc } } | undefined)?.binding?.doc;
    if (next === doc) return;
    doc?.off("beforeObserverCalls", beforeObservers);
    doc?.off("afterTransaction", afterTransaction);
    doc = next;
    doc?.on("beforeObserverCalls", beforeObservers);
    doc?.on("afterTransaction", afterTransaction);
  };

  // A step dispatches only when a front passes a word, about 20 times a second; nothing runs while no agent types.
  const tick = () => {
    frame = 0;
    const state = key.getState(editor.state);
    if (!state?.typing.size || editor.isDestroyed) return;
    const now = performance.now();
    const moved = [...state.typing].some(([client, typing]) => frontOf(editor.state, typing, now) !== shownAt.get(client));
    // The step is a transaction of its own: the presence overlay redraws the agent's caret at the new front with it.
    if (moved) editor.view.dispatch(editor.state.tr.setMeta(key, now).setMeta("addToHistory", false));
    frame = requestAnimationFrame(tick);
  };

  const plugin = new Plugin<RevealState>({
    key,
    state: {
      init: () => empty,
      apply(tr, value, _old, state) {
        const now = tr.getMeta(key) as number | undefined ?? performance.now();
        let typing = new Map(value.typing);
        if (tr.docChanged) {
          // A collaborator's change replaces whole blocks; mapped by what really changed, the typing keeps its ranges.
          const mapping = markdownChangeMapping(tr);
          typing = new Map([...typing].map(([client, entry]) => [client, { ...entry, ranges: entry.ranges
            .map(range => ({ from: mapping.map(range.from, 1), to: mapping.map(range.to, -1) }))
            .filter(range => range.to > range.from) }]));
          const written = writing !== undefined && (tr.getMeta(ySyncPluginKey) as { isChangeOrigin?: boolean } | undefined)?.isChangeOrigin
            ? insertedRanges(mapping) : [];
          if (writing !== undefined && written.length && !prefersReducedMotion(editor.view.dom)) {
            const dom = editor.view.dom;
            const units = written.reduce((sum, range) => sum + range.to - range.from, 0);
            // An even pace of about 50 letters a second, the whole write within one and a half of the AI's loops.
            const duration = Math.min(units * motionMs(dom, "--atlas-motion-fast") / 6, motionMs(dom, "--atlas-motion-loop") * 1.5);
            typing.set(writing, { ranges: written, started: now, duration });
          }
        }
        for (const [client, entry] of typing) if (frontOf(state, entry, now) === undefined) typing.delete(client);
        if (!typing.size && !value.typing.size) return value;
        shownAt = new Map([...typing].map(([client, entry]) => [client, frontOf(state, entry, now)!]));
        return { typing, decorations: DecorationSet.create(state.doc, [...typing.values()].flatMap(entry => unwritten(state, entry, now))) };
      },
    },
    props: { decorations: state => key.getState(state)?.decorations },
    view: view => {
      watch(view.state);
      return {
        update: next => {
          watch(next.state);
          if (key.getState(next.state)?.typing.size) frame ||= requestAnimationFrame(tick);
        },
        destroy: () => watch({} as EditorState),
      };
    },
  });
  editor.registerPlugin(plugin);

  return {
    setAgents: clientIds => { agents = clientIds; },
    front: clientId => shownAt.get(clientId),
    destroy() {
      cancelAnimationFrame(frame);
      if (!editor.isDestroyed) editor.unregisterPlugin(key);
      watch({} as EditorState);
    },
  };
}

/** What a remote transaction inserted, in its new document (its mapping narrowed to what really changed). */
function insertedRanges(mapping: Mapping) {
  const ranges: { from: number; to: number }[] = [];
  mapping.maps.forEach((map, index) => map.forEach((_oldFrom, _oldTo, from, to) => {
    const rest = mapping.slice(index + 1);
    const start = rest.map(from, 1);
    const end = rest.map(to, -1);
    if (end > start) ranges.push({ from: start, to: end });
  }));
  return ranges.sort((a, b) => a.from - b.from);
}

/** The position the typing has reached (snapped to the end of its word), or undefined once all of it is there. */
function frontOf(state: EditorState, typing: Typing, now: number) {
  const total = typing.ranges.reduce((sum, range) => sum + range.to - range.from, 0);
  let left = Math.floor(total * Math.min(1, (now - typing.started) / Math.max(typing.duration, 1)));
  if (left >= total) return undefined;
  for (const range of typing.ranges) {
    const size = range.to - range.from;
    if (left >= size) { left -= size; continue; }
    const at = range.from + left;
    const $at = state.doc.resolve(at);
    if (!$at.parent.isTextblock) return at;
    const rest = $at.parent.textBetween($at.parentOffset, $at.parent.content.size, undefined, "￼");
    const space = rest.search(/\s/);
    return Math.min(range.to, at + (space < 0 ? rest.length : space));
  }
  return undefined;
}

/** The text the typing has not reached yet: there, but invisible. */
function unwritten(state: EditorState, typing: Typing, now: number) {
  const front = frontOf(state, typing, now);
  if (front === undefined) return [];
  return typing.ranges.flatMap(range => {
    const from = Math.max(range.from, front);
    return range.to > from ? [Decoration.inline(from, range.to, { class: "atlas-agent-unwritten" })] : [];
  });
}
