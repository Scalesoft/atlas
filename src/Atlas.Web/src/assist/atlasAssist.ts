// Loaded only through loadAssist.ts, so the assist stylesheet never downloads while assistance is off (plan §12 H.4).
import "@atlas/markdown/assist/style.css";
import { assistMarkersFor } from "@atlas/markdown/assist";
import { watch } from "vue";
import { suggestionTypes } from "../aiPreferences";
import t from "../i18n/assist";
import secrets from "../i18n/secrets";
import { reportAiInteraction } from "../ai/interactions";
import { createModelIntentSource } from "./modelIntents";
import { createWritingActions, undoApplied } from "./writing";
import { writingApi } from "./writingApi";
import type {
  AssistModule,
  MarkdownAssistApplyContext,
  MarkdownAssistBlock,
  MarkdownAssistRange,
  MarkdownAssistResult,
  MarkdownAssistSource,
  MarkdownAssistSuggestion,
} from "@atlas/markdown/assist";
import { registerMarkdownBlockKeyboardHook, type MarkdownPlugin } from "@atlas/markdown/core";
import { contactNode, insertInlineContact, type MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { createDiagramNode } from "@atlas/markdown/plugins/diagrams";
import { insertMathInline } from "@atlas/markdown/plugins/math";
import { insertPageReference } from "@atlas/markdown/plugins/pages";
import { createTocNode } from "@atlas/markdown/plugins/toc";
import { insertYouTrackIssue } from "@atlas/markdown/plugins/youtrack";
import type { Editor } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { calloutAround, codeBlockFromText, diagramSource, objectReplacement, tableFromText, taskListFromParagraph, type JsonNode } from "./conversions";
import { createLinkCandidateClient, createLinkSource, fetchLinkCandidates } from "./linkSuggestions";
import { createIssueResolver, matchContact, resolvePage } from "./resolvers";
import {
  detectDocumentIntents,
  detectIntents,
  fnv1a,
  intentDefinition,
  sessionSecretExamples,
  spanText,
  type CalloutVariant,
  type RuleHit,
  type TextSpan,
} from "./rules";
import { spansOverlap } from "./rules/span";
import { secretSearchText } from "./secretQuery";
import { sectionText } from "./sectionContext";
import type { AtlasAssistContext } from "./types";

/**
 * `atlasAssist`: Atlas content for the editor's optional assist module (plan §12 H.2–H.5). The module draws the
 * marker, the list, the warning bar, the proposal panel and all motion; Atlas supplies what is recognised (the H1
 * rules), confirms it with the authoritative source (resolvers), and writes it with the editor's existing commands.
 * Rule suggestions carry the icon of their type, never the AI glyph, and never set `ai` (§0a).
 *
 * A secret never leaves the browser: detection, spans and hashes are local; the picker query is built from the words
 * around the value (secretQuery.ts); nothing of a block with a secret is sent anywhere, and no lookup is made for a
 * hit that overlaps a detected secret.
 */
export function createAtlasAssist(assist: AssistModule, context: AtlasAssistContext): MarkdownPlugin {
  return {
    id: "assist",
    apiVersion: 1,
    // One runtime per editor: caches and the editor reference never cross editors or sessions.
    create(pluginContext) {
      let editor: Editor | undefined;
      const applied = new WeakMap<MarkdownAssistSuggestion, string>();
      const report = context.reportInteraction ?? reportAiInteraction;
      const automatic = () => context.automaticSuggestionsEnabled?.() !== false;
      const runtime: SourceRuntime = { editor: () => editor, applied, available: automatic };
      const source = createRulesSource(context, runtime);
      // E2 (plan §19): a second source, so a slow or failing lookup never delays the rule suggestions of the block.
      const links = createLinkSource({
        pageId: context.pageId,
        client: createLinkCandidateClient(context.linkCandidates ?? fetchLinkCandidates),
        editor: () => editor,
        enabled: () => automatic() && (context.suggestionTypeEnabled?.("link-mention") ?? true),
        meaning: () => (context.modelSuggestionsEnabled?.() ?? true) && (context.editorLinks?.() ?? true),
        finish: (suggestion, message) => finishSuggestion(runtime, suggestion, message),
      });
      const issues = createIssueResolver(context.resolveIssue);
      const model = createModelIntentSource({
        enabled: () => automatic() && context.modelSuggestionsEnabled?.() === true && context.writing?.intentAllowed() === true,
        pageId: context.pageId, editor: () => editor,
        safe(block) {
          if (!editor || !assist.projectMarkdownAssistSelection) return false;
          const projection = assist.projectMarkdownAssistSelection(editor,{from:block.pos,to:block.pos + block.node.nodeSize});
          projection?.dispose(); return !!projection;
        },
        request: (input, signal) => (context.writing?.api ?? writingApi).intents(input,signal),
        resolve: (hit, block, signal) => isConversion(hit.intent) && !blockShape(editor,block).convertible ? Promise.resolve([]) : new SuggestionBuilder(context,runtime,block,[],issues).forHit(hit,signal),
      });
      const inner = assist.createAssistPlugin({
        sources: [source, links, model],
        ...createWritingActions(assist,context),
        onShown: suggestion => { if (suggestion.ai) report('editor.intent',suggestion.kind,'shown'); },
        onDismissed: suggestion => { if (suggestion.ai) report('editor.intent',suggestion.kind,'dismissed'); },
        ...(context.reducedMotion ? { reducedMotion: context.reducedMotion } : {}),
        onApplied: (suggestion: MarkdownAssistSuggestion) => {
          const message = applied.get(suggestion);
          if (suggestion.ai) report("editor.intent",suggestion.kind,"applied");
          const undo = editor ? undoApplied(editor) : undefined;
          if (message) context.notify(message, undo);
          else if (suggestion.ai) context.notify(t.suggestionApplied, undo);
        },
      }).create(pluginContext);
      return {
        ...inner,
        onCreate(created) {
          editor = created.editor;
          return inner.onCreate?.(created);
        },
        onAttach(attached) {
          const cleanup = inner.onAttach?.(attached);
          const leave = joinBlockKeyboard(attached.editor);
          const stopGates = watch(() => [automatic(), context.modelSuggestionsEnabled?.(), context.writing?.intentAllowed(), context.writing?.transformAllowed(), context.writing?.outlineAllowed(), context.ask?.available(), ...suggestionTypes.map(type => context.suggestionTypeEnabled?.(type.value))], () => {
            if (!attached.editor.isDestroyed) attached.editor.view.dispatch(attached.editor.state.tr.setMeta('atlasAssistPolicy',true).setMeta('addToHistory',false));
          }, {flush:'sync'});
          // E4: the `[[` list may order by the section the author writes in (never a block with a password).
          const live = attached.editor;
          const withdraw = context.registerSection?.(() => live.isDestroyed ? null : sectionText(live.state.doc, live.state.selection.head));
          return () => {
            stopGates();
            withdraw?.();
            leave();
            if (typeof cleanup === "function") cleanup();
          };
        },
      };
    },
  };
}

// --- Alt+F10 (decision O5) --------------------------------------------------------------------------------------

/**
 * Puts the block's assist markers into the grip's keyboard path (Alt+F10, then Tab; core hook since editor 0.9.0).
 * Markers live in the module's body-level overlay with `data-md-assist-marker` and no reference to their block, so a
 * marker belongs to the block its vertical centre lies in (the module positions it at the block's first line).
 */
function joinBlockKeyboard(editor: Editor): () => void {
  return registerMarkdownBlockKeyboardHook(editor, { id: "atlas-assist-markers", controls: block => markersOf(editor, block.pos) });
}

/** Ownership comes from the module's actual block association, never overlay geometry. */
export const markersOf = assistMarkersFor;

// --- The rules source ---------------------------------------------------------------------------------------------

interface SourceRuntime {
  readonly editor: () => Editor | undefined;
  readonly available?: () => boolean;
  /** The toast after Použít, per suggestion (none for a suggestion that only opens a picker). */
  readonly applied: WeakMap<MarkdownAssistSuggestion, string>;
}

/** The toast after Použít, and the author's caret kept where they were typing (except when the write hands it over). */
function finishSuggestion(runtime: SourceRuntime, suggestion: MarkdownAssistSuggestion, message?: string, caret: "keep" | "follow" = "keep"): MarkdownAssistSuggestion {
  const finished = { ...suggestion, caret, available: () => runtime.available?.() !== false && suggestion.available?.() !== false };
  if (message) runtime.applied.set(finished, message);
  return finished;
}

/** Text offsets of the pasted range inside the block (E6). */
function pastedSpan(block: MarkdownAssistBlock): TextSpan | undefined {
  if (!block.pasted) return undefined;
  const start = Math.max(0, block.pasted.from - block.pos - 1);
  const end = Math.min(block.text.length, block.pasted.to - block.pos - 1);
  return end > start ? { start, end } : undefined;
}

export function createRulesSource(context: AtlasAssistContext, runtime: SourceRuntime): MarkdownAssistSource {
  const issues = createIssueResolver(context.resolveIssue);
  return {
    id: "atlas-rules",
    async suggest(block: MarkdownAssistBlock, { signal }: { readonly signal: AbortSignal }) {
      if (context.automaticSuggestionsEnabled?.() === false) return [];
      let hits: RuleHit[];
      try {
        const pasted = pastedSpan(block);
        hits = detectIntents(block.text, pasted ? { pasted } : {});
      } catch {
        // A rule bug (spanOf throws) shows nothing rather than a suggestion pointing elsewhere.
        return [];
      }
      const secrets = hits.filter(hit => hit.intent === "secret.plaintext").map(hit => hit.span);
      const editor = runtime.editor();
      const shape = blockShape(editor, block);
      const build = new SuggestionBuilder(context, runtime, block, secrets, issues);
      const groups = await Promise.all(hits.map(async hit => {
        if (!intentEnabled(context, hit.intent)) return [];
        // Nothing near a detected secret is looked up anywhere, and no conversion rewrites a block that holds one.
        if (hit.intent !== "secret.plaintext" && secrets.some(span => spansOverlap(span, hit.span))) return [];
        if (spanText(block.text, hit.span).includes(objectReplacement) && hit.span.end - hit.span.start < block.text.length) return [];
        if (isConversion(hit.intent) && (!shape.convertible || secrets.length)) return [];
        if (hit.intent === "secret.reference" && secrets.length) return [];
        try {
          const suggestions: MarkdownAssistResult[] = await build.forHit(hit, signal);
          if (suggestions.length || hit.intent !== "youtrack.issue" || !hit.query || !issues.isKnownMissing(hit.query)) return suggestions;
          const key = hit.query;
          return [{
            id: `youtrack.unverified:${fnv1a(key)}`,
            kind: "youtrack.unverified",
            title: t.youtrackUnverified.title(key),
            description: t.youtrackUnverified.description,
            icon: "ticket",
            informational: true,
            range: block.rangeOf(hit.span.start, hit.span.end),
            reason: t.youtrackUnverified.reason,
            available: () => runtime.available?.() !== false && intentEnabled(context, hit.intent),
          } satisfies MarkdownAssistResult];
        } catch {
          return [];
        }
      }));
      const suggestions = groups.flat();
      if (shape.heading && editor) suggestions.push(...build.documentLevel(editor));
      return signal.aborted ? [] : suggestions;
    },
  };
}

/** Preference categories cover both rules and model hints; secret warnings remain a separate safety feature. */
function intentEnabled(context: AtlasAssistContext, intent: string): boolean {
  const type = intent.startsWith("contact.") ? "contact"
    : intent.startsWith("page.") ? "page-link"
    : intent.startsWith("youtrack.") ? "issue"
    : intent.startsWith("secret.") ? null : "structure";
  return type === null || context.suggestionTypeEnabled?.(type) !== false;
}

const conversions = new Set(["table.fromText", "tasklist.fromText", "callout.wrap", "code.fromText", "diagram.fromText", "contact.card"]);
const isConversion = (intent: string) => conversions.has(intent);

/** Conversions apply to a top-level paragraph only (not a heading, a code block, a cell or a list item). */
function blockShape(editor: Editor | undefined, block: MarkdownAssistBlock) {
  const type = block.node.type.name;
  let topLevel = true;
  if (editor) {
    try { topLevel = editor.state.doc.resolve(block.pos).depth === 0; } catch { topLevel = false; }
  }
  return { convertible: type === "paragraph" && topLevel, heading: type === "heading" };
}

const calloutLabels: Record<CalloutVariant, string> = {
  note: "Poznámka", tip: "Tip", important: "Důležité", warning: "Upozornění", caution: "Pozor",
};

/** The document range of the whole textblock holding `range` (a conversion replaces the block, not its text). */
function blockRange(editor: Editor, range: MarkdownAssistRange): MarkdownAssistRange {
  const $from = editor.state.doc.resolve(range.from);
  return { from: $from.before(), to: $from.after() };
}
/** One character per position, as the module's `block.text`: a hard break is `\n`, any other inline atom U+FFFC. */
const leafText = (leaf: ProseMirrorNode) => leaf.type.name === "hardBreak" ? "\n" : objectReplacement;
const currentText = (editor: Editor, range: MarkdownAssistRange) => editor.state.doc.textBetween(range.from, range.to, "\n", leafText);
const textOf = (node: ProseMirrorNode) => node.textBetween(0, node.content.size, "\n", leafText);

function contactLine(contact: MarkdownContact): string {
  return [contact.internalUser?.name ?? contact.name, contact.department, contact.organization, contact.emails?.[0]].filter(Boolean).join(" · ");
}

const emailPattern = /(?<![A-Za-z0-9._%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+/g;
const phonePattern = /(?<![\p{N}+])(?:\+\d{2,3}[  ]?)?\d{3}[  ]?\d{3}[  ]?\d{3}(?!\p{N})/gu;

/** A card from the block's own text when the directory does not know the person (an external supplier). */
function contactFromText(text: string, name: string | undefined): MarkdownContact | null {
  const emails = [...new Set(text.match(emailPattern) ?? [])];
  const phones = [...new Set(text.match(phonePattern) ?? [])];
  const display = name && !name.includes("@") ? name : emails[0];
  if (!display) return null;
  return { name: display, kind: "external", ...(emails.length ? { emails } : {}), ...(phones.length ? { phones } : {}) };
}

class SuggestionBuilder {
  private readonly pageId: string;
  constructor(
    private readonly context: AtlasAssistContext,
    private readonly runtime: SourceRuntime,
    private readonly block: MarkdownAssistBlock,
    private readonly secrets: readonly TextSpan[],
    private readonly issues: ReturnType<typeof createIssueResolver>,
  ) {
    this.pageId = context.pageId() ?? "";
  }

  private span(hit: RuleHit) { return spanText(this.block.text, hit.span); }
  private range(hit: RuleHit) { return this.block.rangeOf(hit.span.start, hit.span.end); }
  private id(hit: RuleHit, kind: string = hit.intent) { return `${kind}:${fnv1a(this.span(hit))}`; }
  private reason(hit: RuleHit) { return t.catalog[hit.intent] ?? hit.intent; }
  private done(suggestion: MarkdownAssistSuggestion, message?: string, caret: "keep" | "follow" = "keep"): MarkdownAssistSuggestion {
    return finishSuggestion(this.runtime, {
      ...suggestion,
      available: () => intentEnabled(this.context, suggestion.kind) && suggestion.available?.() !== false,
    }, message, caret);
  }

  async forHit(hit: RuleHit, signal: AbortSignal): Promise<MarkdownAssistSuggestion[]> {
    if (!intentEnabled(this.context, hit.intent)) return [];
    switch (hit.intent) {
      case "secret.plaintext": return this.plaintextSecret(hit);
      case "youtrack.issue": return this.issue(hit, signal);
      case "page.link": return this.page(hit, signal);
      case "contact.inline": return this.contact(hit, signal);
      case "contact.card": return this.contactCard(hit, signal);
      case "secret.reference": return this.secretReference(hit, signal);
      case "table.fromText": return this.table(hit);
      case "tasklist.fromText": return this.taskList(hit);
      case "callout.wrap": return this.callout(hit);
      case "code.fromText": return this.code(hit);
      case "diagram.fromText": return this.diagram(hit);
      case "math.inline": return this.math(hit);
      default: return [];
    }
  }

  /** E7: a persistent warning; „Přesunout do trezoru“ replaces the span with a reference picked in the vault. */
  private plaintextSecret(hit: RuleHit): MarkdownAssistSuggestion[] {
    const examples = this.context.examples ?? sessionSecretExamples;
    if (examples.isExample(this.pageId, this.span(hit))) return [];
    const initialQuery = secretSearchText(this.block.text, this.secrets);
    const { context, pageId } = this;
    return [this.done({
      id: this.id(hit),
      kind: "secret.plaintext",
      title: t.plaintext.title,
      description: t.plaintext.description,
      icon: "triangle-alert",
      tone: "warning",
      applyLabel: t.plaintext.apply,
      applyIcon: "lock",
      range: this.range(hit),
      reason: t.plaintext.reason,
      apply: (apply: MarkdownAssistApplyContext) => moveSpanToVault(apply, context, initialQuery),
      secondary: [{
        id: "example",
        label: t.plaintext.example,
        run(action: MarkdownAssistApplyContext) {
          // Only a hash of the span is remembered, for this page and browser session (examples.ts).
          if (action.range) examples.mark(pageId, currentText(action.editor, action.range));
        },
      }],
    })];
  }

  private async issue(hit: RuleHit, signal: AbortSignal): Promise<MarkdownAssistSuggestion[]> {
    const key = hit.query;
    if (!key) return [];
    const issue = await this.issues.resolve(key, signal);
    if (!issue) return [];
    return [this.done({
      id: this.id(hit),
      kind: "youtrack.issue",
      title: t.issue.title(key),
      description: [issue.title, issue.state].filter(Boolean).join(" · "),
      icon: "ticket",
      range: this.range(hit),
      reason: t.issue.reason(key),
      apply: (apply: MarkdownAssistApplyContext) => !!apply.range && apply.dispatch(insertYouTrackIssue(issue), { replaceRange: apply.range }),
    }, t.issue.done)];
  }

  /** E6: a pasted `#/pages/<id>` URL becomes a page reference that follows renames and availability. */
  private async page(hit: RuleHit, signal: AbortSignal): Promise<MarkdownAssistSuggestion[]> {
    if (!hit.query) return [];
    const page = await resolvePage(this.context.pages, hit.query, signal);
    if (!page) return [];
    return [this.done({
      id: this.id(hit),
      kind: "page.link",
      title: t.page.title(page.title),
      description: t.page.description,
      icon: "link",
      range: this.range(hit),
      reason: t.page.reason,
      apply: (apply: MarkdownAssistApplyContext) => !!apply.range && apply.dispatch(insertPageReference(page), { replaceRange: apply.range }),
    }, t.page.done)];
  }

  private async contact(hit: RuleHit, signal: AbortSignal): Promise<MarkdownAssistSuggestion[]> {
    const written = this.span(hit);
    const match = await matchContact(this.context.contacts, hit.query ?? written, signal);
    const reason = t.contact.reason;
    if (match.kind === "none") {
      if (!written.includes("@")) return [];
      const href = `mailto:${written}`;
      let intersectsLink = false;
      this.block.node.nodesBetween(hit.span.start, hit.span.end, node => {
        if (node.isText && node.marks.some(mark => mark.type.name === "link")) intersectsLink = true;
        return !intersectsLink;
      });
      if (intersectsLink) return [];
      return [this.done({
        id: this.id(hit, "contact.email-link"),
        kind: "contact.email-link",
        title: t.contact.emailTitle,
        description: t.contact.emailDescription,
        icon: "link",
        range: this.range(hit),
        reason: t.contact.emailReason,
        apply: (apply: MarkdownAssistApplyContext) => {
          if (!apply.range) return false;
          apply.editor.commands.setTextSelection(apply.range);
          return apply.dispatch({ type: "set-link", href });
        },
      }, t.contact.emailDone)];
    }
    if (match.kind === "many") {
      const query = hit.query ?? written;
      // The existing `@` list with the name already typed: the author picks the person there.
      return [this.done({
        id: this.id(hit, "contact.pick"),
        kind: "contact.inline",
        title: t.contact.pickTitle,
        description: t.contact.pickDescription(query),
        icon: "user",
        range: this.range(hit),
        reason,
        apply: (apply: MarkdownAssistApplyContext) => !!apply.range && apply.editor.chain().focus().insertContentAt(apply.range, { type: "text", text: `@${query}` }).run(),
      }, undefined, "follow")];
    }
    const { contact } = match;
    return [
      this.done({
        id: this.id(hit),
        kind: "contact.inline",
        title: t.contact.replaceTitle(written),
        description: contactLine(contact),
        avatar: { initials: (contact.internalUser?.name ?? contact.name).split(/\s+/).map(word=>word[0]).slice(0,2).join("") },
        icon: "user",
        range: this.range(hit),
        reason,
        apply: (apply: MarkdownAssistApplyContext) => !!apply.range && apply.dispatch(insertInlineContact(contact), { replaceRange: apply.range }),
      }, t.contact.done),
      this.done({
        id: this.id(hit, "contact.card"),
        kind: "contact.card",
        title: t.contact.cardBelowTitle,
        description: contactLine(contact),
        avatar: { initials: (contact.internalUser?.name ?? contact.name).split(/\s+/).map(word=>word[0]).slice(0,2).join("") },
        icon: "users",
        range: this.range(hit),
        reason,
        apply: (apply: MarkdownAssistApplyContext) => !!apply.range
          && apply.editor.commands.insertContentAt(blockRange(apply.editor, apply.range).to, contactNode(contact)),
      }, t.contact.done),
    ];
  }

  /** A block with a name, an e-mail, a phone or a position: the card from the directory, or from the text itself. */
  private async contactCard(hit: RuleHit, signal: AbortSignal): Promise<MarkdownAssistSuggestion[]> {
    const match = hit.query ? await matchContact(this.context.contacts, hit.query, signal) : { kind: "none" as const };
    const contact = match.kind === "one" ? match.contact : contactFromText(this.block.text, hit.query);
    if (!contact) return [];
    return [this.done({
      id: this.id(hit),
      kind: "contact.card",
      title: t.contact.convertTitle,
      description: contactLine(contact),
      icon: "users",
      range: this.range(hit),
      reason: t.contact.convertReason,
      apply: (apply: MarkdownAssistApplyContext) => this.propose(apply, t.contact.convertTitle, contactNode(contact) as JsonNode),
    }, t.contact.done)];
  }

  /** „Heslo je v KeePassu…“: a reference to a stored secret, offered only when the reader may Discover a match. */
  private async secretReference(hit: RuleHit, signal: AbortSignal): Promise<MarkdownAssistSuggestion[]> {
    const query = hit.query;
    if (!query || !(await this.context.hasSecrets(query, signal))) return [];
    const { context } = this;
    return [this.done({
      id: this.id(hit),
      kind: "secret.reference",
      title: t.secret.title,
      description: t.secret.description(query),
      icon: "key-round",
      range: this.range(hit),
      reason: t.secret.reason,
      async apply(apply: MarkdownAssistApplyContext) {
        const picked = await context.pickSecret({ mode: "pick", initialQuery: query, signal: apply.signal });
        const range = apply.range;
        if (!picked || !range || apply.signal.aborted) return false;
        const editor = apply.editor;
        if (picked.inline) {
          // The reference right after the words, then the space before it: one undo step (the insert command closes
          // the history before itself, the adjacent space joins it).
          editor.commands.setTextSelection(range.to);
          if (!apply.controller.dispatch(picked.command)) return false;
          return editor.commands.insertContentAt(range.to, " ");
        }
        // A card goes under the block: inserted at the end of the block's text, it lands after the paragraph.
        editor.commands.setTextSelection(blockRange(editor, range).to - 1);
        return apply.controller.dispatch(picked.command);
      },
    }, t.secret.done)];
  }

  private propose(apply: MarkdownAssistApplyContext, title: string, after: JsonNode): Promise<boolean> | boolean {
    if (!apply.range) return false;
    const range = blockRange(apply.editor, apply.range);
    return apply.propose({ title, before: currentText(apply.editor, apply.range), after, range });
  }

  /** Writes a conversion directly (no preview): the attempt is one undo step and glows. */
  private write(apply: MarkdownAssistApplyContext, after: (node: ProseMirrorNode) => JsonNode | null): boolean {
    if (!apply.range) return false;
    const range = blockRange(apply.editor, apply.range);
    const node = apply.editor.state.doc.nodeAt(range.from);
    const content = node && after(node);
    return !!content && apply.editor.commands.insertContentAt(range, content);
  }

  private table(hit: RuleHit): MarkdownAssistSuggestion[] {
    const table = tableFromText(this.block.text);
    if (!table) return [];
    const rows = table.content!.length;
    return [this.done({
      id: this.id(hit),
      kind: "table.fromText",
      title: t.table.title,
      description: t.table.description(rows),
      icon: "table",
      range: this.range(hit),
      reason: t.table.reason,
      apply: (apply: MarkdownAssistApplyContext) => this.propose(apply, t.table.title, table),
    }, t.table.done)];
  }

  private taskList(hit: RuleHit): MarkdownAssistSuggestion[] {
    if (!taskListFromParagraph((this.block.node.toJSON() as JsonNode).content)) return [];
    return [this.done({
      id: this.id(hit),
      kind: "tasklist.fromText",
      title: t.tasklist.title,
      icon: "list-checks",
      range: this.range(hit),
      reason: t.tasklist.reason,
      apply: (apply: MarkdownAssistApplyContext) => this.write(apply, node => taskListFromParagraph((node.toJSON() as JsonNode).content)),
    }, t.tasklist.done)];
  }

  private callout(hit: RuleHit): MarkdownAssistSuggestion[] {
    const variant = hit.callout ?? "note";
    return [this.done({
      id: this.id(hit),
      kind: "callout.wrap",
      title: t.callout.title,
      description: calloutLabels[variant],
      icon: "callout",
      range: this.range(hit),
      reason: t.callout.reason(calloutLabels[variant]),
      apply: (apply: MarkdownAssistApplyContext) => this.write(apply, node => calloutAround(node.toJSON() as JsonNode, variant)),
    }, t.callout.done)];
  }

  private code(hit: RuleHit): MarkdownAssistSuggestion[] {
    const code = codeBlockFromText(this.block.text);
    if (!code) return [];
    const language = code.attrs?.language as string | null;
    return [this.done({
      id: this.id(hit),
      kind: "code.fromText",
      title: t.code.title,
      description: t.code.description(language),
      icon: "square-code",
      range: this.range(hit),
      reason: t.code.reason,
      apply: (apply: MarkdownAssistApplyContext) => this.write(apply, node => codeBlockFromText(textOf(node))),
    }, t.code.done)];
  }

  private diagram(hit: RuleHit): MarkdownAssistSuggestion[] {
    const source = diagramSource(this.block.text);
    if (!source) return [];
    return [this.done({
      id: this.id(hit),
      kind: "diagram.fromText",
      title: t.diagram.title,
      description: t.diagram.description,
      icon: "workflow",
      range: this.range(hit),
      reason: t.diagram.reason,
      apply: (apply: MarkdownAssistApplyContext) => this.propose(apply, t.diagram.title, createDiagramNode({ version: 1, source, caption: "" }) as JsonNode),
    }, t.diagram.done)];
  }

  /** Not detected by the phase 1 rules (catalog `phase1: false`); a later source (phase 4) maps to this command. */
  private math(hit: RuleHit): MarkdownAssistSuggestion[] {
    const expression = (hit.query ?? this.span(hit)).replace(/^\$|\$$/g, "").trim();
    if (!expression) return [];
    return [this.done({
      id: this.id(hit),
      kind: "math.inline",
      title: t.math.title(this.span(hit)),
      icon: "sigma",
      range: this.range(hit),
      reason: t.math.reason,
      apply: (apply: MarkdownAssistApplyContext) => !!apply.range && apply.dispatch(insertMathInline({ version: 1, expression }), { replaceRange: apply.range }),
    }, t.math.done)];
  }

  /** A table of contents at the start, offered at a heading of a document with six or more H2 and none yet. */
  documentLevel(editor: Editor): MarkdownAssistSuggestion[] {
    if (!intentEnabled(this.context, "toc.insert")) return [];
    const headings: { level: number }[] = [];
    let hasToc = false;
    editor.state.doc.descendants(node => {
      if (node.type.name === "heading") headings.push({ level: Number(node.attrs.level) });
      if (node.type.name === "tableOfContents") hasToc = true;
      return !node.isTextblock;
    });
    if (!detectDocumentIntents({ headings, hasToc }).length) return [];
    return [this.done({
      id: `toc.insert:${headings.length}`,
      kind: "toc.insert",
      title: t.toc.title,
      description: t.toc.description(headings.filter(heading => heading.level === 2).length),
      icon: "outline",
      reason: t.toc.reason,
      apply: (apply: MarkdownAssistApplyContext) => apply.editor.commands.insertContentAt(0, createTocNode({ version: 1, title: "Obsah dokumentu", minLevel: 1, maxLevel: 3 })),
    }, t.toc.done)];
  }
}

/**
 * „Přesunout do trezoru“ in replace mode (decision O2): the author stores the password in KeePass and picks it in the
 * SecretPicker; the span is concealed (dots, ~700 ms, decoration only) and replaced by an inline reference. The value
 * is never sent: the picker gets the words around it, and the write is the reference (`secretId`) alone.
 */
async function moveSpanToVault(apply: MarkdownAssistApplyContext, context: AtlasAssistContext, initialQuery: string): Promise<boolean> {
  const picked = await context.pickSecret({ mode: "replace", initialQuery, signal: apply.signal });
  if (!picked || apply.signal.aborted) return false;
  const stale = () => { context.warn(secrets.staleProposal); return false; };
  if (!apply.range) return stale();
  await apply.effects.conceal(apply.range);
  const range = apply.range;
  if (!range || apply.signal.aborted) return stale();
  if (!apply.dispatch(picked.command, { replaceRange: range })) return false;
  context.notify(secrets.moved(picked.vaultName), undoApplied(apply.editor));
  return true;
}
