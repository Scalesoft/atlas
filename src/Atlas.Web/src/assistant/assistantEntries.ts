import { focusWhenPresent } from "../keyboard/navigation";
import { glow, scrollBehavior } from "../motion/aiMotion";
import { unfoldTo } from "../editor/headingFold";
import { aiTarget } from "../assist/aiIntents";
import t from "../i18n/assistant";
import { NO_CONTEXT, QUESTION_MAX_LENGTH, selectionPrefix, useAssistant, type AssistantContext } from "./useAssistant";

// The entries of the Assistant that App.vue wires (plan phase 3 §15): what the screen is about, the two palette commands and
// the jump of a citation. The logic lives here; App.vue only passes what it knows and renders the result.

export interface ScreenFacts {
  /** A page is on screen in the reading view (no editor, no settings, no library). */
  documentOpen: boolean;
  editing: boolean;
  page?: { id: string; title: string; spaceId: string; isPublished: boolean } | null;
  space?: { id: string; name: string } | null;
  /** The person may write in that space (wiki tasks are offered only then). */
  canWrite?: boolean;
}

/**
 * What the next question is about. The page is part of it only while a published page is being read (O12: in the editor
 * the answer would come from a different text than the one on screen); the space is the page's or the one being browsed.
 */
export function assistantContextFor({ documentOpen, editing, page, space, canWrite }: ScreenFacts): AssistantContext {
  const reading = documentOpen && !editing && !!page?.isPublished;
  const spaceFor = space && (!page || page.spaceId === space.id || !documentOpen) ? space : null;
  if (!reading && !spaceFor) return NO_CONTEXT;
  return {
    pageId: reading ? page!.id : null,
    pageTitle: reading ? page!.title : null,
    spaceId: spaceFor?.id ?? null,
    spaceName: spaceFor?.name ?? null,
    canWrite: !!canWrite && !!spaceFor,
  };
}

/**
 * A wiki task for the agent: a short command (palette, panel) and the instruction it sends. The agent only proposes, the person
 * confirms on the card. The wording is chosen so the server's tool groups (AssistantToolGroups) pick the write tools: no
 * „navrhni“ / „napiš“ in a move task (they would add drafts and push the moves out of the tool budget).
 */
export interface WikiTask { id: string; label: string; scope: "page" | "space"; prompt: string }

/**
 * The tasks that fit the screen: none without write rights; page tasks only while reading a published page.
 * `arrange`: the admin left the batch move of the left menu on (capability `arrangePages`).
 */
export function wikiTasks(context: AssistantContext, arrange = false): WikiTask[] {
  if (!context.canWrite) return [];
  const tasks: WikiTask[] = [];
  if (context.pageId) tasks.push(
    { id: "task-contacts", label: t.tasks.contacts, scope: "page",
      prompt: "Projdi tuto stránku a všechny kontaktní údaje (jména s telefony, e-maily a funkcemi) převeď na kontakty. Ostatní text nech beze změny." },
    { id: "task-page-labels", label: t.tasks.pageLabels, scope: "page",
      prompt: "Nastav této stránce vhodnou ikonu a tagy podle jejího obsahu. Ikonu vyber z kandidátů, které vrátí atlas_list_appearance s pageId této stránky." },
  );
  if (context.spaceId) {
    const space = context.spaceName ?? "tento prostor";
    tasks.push(
      { id: "task-space-icons", label: t.tasks.spaceIcons, scope: "space",
        prompt: `Doplň ikony stránkám v prostoru ${space}, které ještě ikonu nemají. Vyber je podle obsahu stránek (kandidáty vrací atlas_list_appearance s pageId stránky).` },
      { id: "task-space-tags", label: t.tasks.spaceTags, scope: "space",
        prompt: `Otaguj stránky v prostoru ${space} jednotnými tagy podle jejich obsahu.` },
    );
    if (arrange) tasks.push(
      { id: "task-space-menu", label: t.tasks.spaceMenu, scope: "space",
        prompt: `Poskládej v prostoru ${space} srozumitelné levé menu: přečti strom prostoru, popiš, jak bys stránky seřadil a zanořil (související pohromadě, nejdůležitější nahoře), a celé mi to dej k potvrzení najednou.` },
    );
  }
  return tasks;
}

/** Runs a task as a new agent conversation about its page or space; false when the panel could not take it. */
export function runWikiTask(context: AssistantContext, task: WikiTask) {
  const assistant = useAssistant();
  if (!assistant.openFor(context)) return Promise.resolve(false);
  assistant.newConversation();
  assistant.tools.value = true;
  assistant.chooseScope(task.scope);
  return assistant.ask(task.prompt);
}

export interface EntryCommand { id: string; label: string; detail: string; icon: "ai"; run: () => unknown }

/**
 * „Asistent“ and „Zeptat se na tuto stránku“; none without the `assistant` capability or, for the second, outside reading a
 * published page or while no question can be answered now (`canAsk`, P11). With the `agent` capability the wiki tasks of the screen follow; each starts its conversation at once.
 */
export function assistantCommands(enabled: boolean, context: AssistantContext, open: (context: AssistantContext, onPage: boolean) => void, agent = false, arrange = false, canAsk = enabled): EntryCommand[] {
  if (!enabled) return [];
  const commands: EntryCommand[] = [{ id: "assistant", label: t.entry.assistant, detail: t.entry.assistantDetail, icon: "ai", run: () => open(context, false) }];
  // An ask over the page follows the one rule (aiIntentAvailable: the agent alone answers only with „S nástroji“ on); the panel stays.
  if (context.pageId && canAsk) commands.push({ id: "ask-page", label: t.entry.askPage, detail: context.pageTitle ?? t.entry.currentPage, icon: "ai", run: () => open(context, true) });
  if (agent) for (const task of wikiTasks(context, arrange)) commands.push({
    id: task.id, label: task.label, detail: t.entry.taskDetail(task.scope === "page" ? context.pageTitle ?? t.scope.page : context.spaceName ?? t.scope.spaceDefault), icon: "ai",
    run: () => runWikiTask(context, task),
  });
  return commands;
}

/** Opens the panel for `context`; `onPage` also narrows the scope to the page („Zeptat se na stránku“). */
export function openAssistant(context: AssistantContext, onPage = false) {
  const assistant = useAssistant();
  if (!assistant.openFor(context)) return false;
  if (onPage) assistant.chooseScope("page");
  void focusComposer();
  return true;
}

/** Asked for, the assistant takes the caret: also when its code still loads or it opens in a page dock that was already open. */
async function focusComposer() {
  await import("../components/assistant/AssistantPanel.vue");
  // The async component renders a few frames after its chunk, when WorkspaceSidePanel has already settled on its surface.
  await new Promise(requestAnimationFrame);
  if (!document.querySelector("dialog[open]")) await focusWhenPresent(document.body, "[data-assistant-composer] textarea"); // a palette hands focus over itself
}

export type SelectionAction = "ask" | "explain" | "summarize";
/** The prompts of the selection toolbar's one-click actions; the selection follows on the next line, as the question's text. */
export const SELECTION_PROMPTS: Record<Exclude<SelectionAction, "ask">, string> = {
  explain: "Vysvětli tento výběr ze stránky:",
  summarize: "Shrň tento výběr ze stránky:",
};

/**
 * The selection toolbar of a read page (refresh 2.5, board „Asistent – vstupy a průběh“) hands the selected text to the
 * assistant, all through the one assistant capability (a chat about the page):
 * - `ask` opens the panel with the selection attached to the next question (a chip above the composer) and the page as scope;
 *   the person writes the question;
 * - `explain` and `summarize` start a new conversation about the page and ask at once with a fixed prompt.
 * Returns whether the panel took it (no capability, an outage or a too long selection: nothing happens, the conversation stays).
 */
export async function selectionToAssistant(context: AssistantContext, action: SelectionAction, text: string, target?: string) {
  const assistant = useAssistant();
  const question = action === "ask" ? `${selectionPrefix(target)}${text.trim()}` : `${targetPrompt(action, target)}\n${text}`;
  if (!text.trim() || question.length >= QUESTION_MAX_LENGTH || !assistant.openFor(context)) return false;
  if (action === "ask") return assistant.attachSelection(text, target);
  assistant.newConversation();
  assistant.chooseScope("page");
  return assistant.ask(question);
}
/** The prompt names what was pointed at (P11: „Shrň tento obsah (sekce „Úvod“):“), cut to fit; a plain selection keeps the toolbar's words. */
export function targetPrompt(action: Exclude<SelectionAction, "ask">, target?: string) {
  if (!target || target === "Výběr") return SELECTION_PROMPTS[action];
  const name = aiTarget(target);
  return `${action === "explain" ? "Vysvětli" : "Shrň"} tento obsah (${name.charAt(0).toLocaleLowerCase("cs") + name.slice(1)}):`;
}

/**
 * P11 Zeptat se / Vysvětlit / Shrnout from any surface (the read page's selection bar, the page editor, a board item): the text to
 * the assistant about what is on screen, the keyboard to its question field. `target` names what was pointed at („Sekce „Úvod““).
 * The caller checks `canAnswer` and the text (aiText). While editing the page scope is not offered (O12), so the space answers.
 */
export async function askAssistant(action: SelectionAction, text: string, target?: string) {
  const taken = await selectionToAssistant(useAssistant().context.value, action, text, target);
  if (taken) await focusComposer();
  return taken;
}

/**
 * The cited section lights up where the citation led: the page is already open (the router jumped to the anchor), so the
 * heading is scrolled to and glows once. The editor mounts lazily, so the heading is awaited for about two seconds.
 */
export function revealCitedHeading(headingId: string | null | undefined, root: ParentNode = document): Promise<boolean> {
  if (!headingId) return Promise.resolve(false);
  const escaped = headingId.replace(/["\\]/g, (char) => `\\${char}`);
  const selector = `.document-body [data-heading-id="${escaped}"]`;
  return new Promise((resolve) => {
    let frames = 0;
    const look = () => {
      const heading = root.querySelector<HTMLElement>(selector);
      if (heading) {
        unfoldTo(heading);
        if (typeof heading.scrollIntoView === "function") heading.scrollIntoView({ block: "center", behavior: scrollBehavior(heading) });
        glow(heading);
        resolve(true);
      } else if (++frames > 120) resolve(false);
      else requestAnimationFrame(look);
    };
    look();
  });
}
