import { plural, translate } from ".";

/**
 * The assistant's words the eager code needs (assistant/, the launcher): scope, notices, the agent's steps, wiki tasks, entries, the intents'
 * names. Small on purpose; the panel, thread and proposals are in assistant-panel.ts. The Czech source. What goes to the
 * model (the prompts of the wiki tasks and of the selection actions, the selection prefix) is not here: it stays Czech in the code.
 */
const cs = {
  title: "Asistent",
  intents: { ask: "Zeptat se", explain: "Vysvětlit", summarize: "Shrnout", rewrite: "Přepsat", translate: "Přeložit", complete: "Doplnit", links: "Navrhnout odkazy a kontakty" },
  scope: {
    page: "Tato stránka",
    space: (name?: string | null) => name ? `Prostor ${name}` : "Tento prostor",
    spaceDefault: "Tento prostor",
    wiki: "Celá dostupná wiki",
    wikiShort: "Celá wiki",
    pageTitled: (title: string) => `Stránka „${title}“`,
    changed: (label: string) => `Rozsah změněn na: ${label}`,
  },
  notice: {
    secretQuery: "Dotaz vypadá, že obsahuje heslo. Odeberte ho před odesláním.",
    toolsOnly: "Odpovědi z wiki teď nejsou k dispozici. Zapněte „S nástroji“ a asistent si stránky přečte sám.",
    outdated: "Tato konverzace používá předchozí nastavení.",
    unavailableConversation: "Konverzace není dostupná",
    loadFailed: "Konverzaci se nepodařilo načíst.",
    deleteFailed: "Konverzaci se nepodařilo smazat.",
    deleteAllFailed: "Konverzace se nepodařilo smazat.",
    listFailed: "Konverzace se nepodařilo načíst.",
  },
  agent: {
    verbs: {
      atlas_search: "Hledám",
      atlas_get_page: "Čtu stránku",
      atlas_get_space_tree: "Procházím strom prostoru",
      atlas_find_mentions: "Hledám zmínky",
      atlas_explain_access: "Zjišťuji přístupy",
      atlas_list_spaces: "Procházím prostory",
      atlas_list_pages: "Procházím stránky",
      atlas_get_labels: "Čtu ikonu a tagy",
      atlas_list_appearance: "Procházím ikony",
      atlas_find_pages_by_tag: "Hledám stránky s tagem",
      atlas_suggest_tags: "Hledám tagy",
      atlas_get_backlinks: "Hledám odkazy",
      atlas_get_history: "Čtu historii",
      atlas_list_attachments: "Procházím přílohy",
      atlas_get_attachment: "Čtu přílohu",
      atlas_apply_labels: "Připravuji návrh",
      atlas_create_draft: "Navrhuji novou stránku",
      atlas_update_section: "Navrhuji změnu stránky",
      atlas_insert_section: "Navrhuji změnu stránky",
      atlas_replace_page: "Navrhuji změnu stránky",
      atlas_rename_page: "Navrhuji změnu stránky",
    } as Record<string, string>,
    fallbackVerb: "Zjišťuji informace",
    interrupted: "Odpověď asistenta se přerušila.",
    failed: "Odpověď asistenta se nepodařila.",
  },
  tasks: {
    contacts: "Převést kontaktní údaje na kontakty",
    pageLabels: "Doplnit ikonu a tagy stránky",
    spaceIcons: "Doplnit ikony stránkám prostoru",
    spaceTags: "Otagovat stránky prostoru",
    spaceMenu: "Poskládat levé menu prostoru",
  },
  entry: {
    assistant: "Asistent",
    assistantDetail: "Zeptat se na obsah wiki",
    askPage: "Zeptat se na tuto stránku",
    currentPage: "Aktuální stránka",
    taskDetail: (where: string) => `Asistent · ${where}`,
    selection: "Výběr",
  },
  board: {
    board: "Tabule",
    item: "Prvek tabule",
    items: (count: number) => `${plural(count, { one: "# prvek", few: "# prvky", other: "# prvků" })} tabule`,
  },
  launcher: { sidebar: "Asistent", askPage: "Zeptat se na tuto stránku" },
};

export type AssistantMessages = typeof cs;
export default translate(cs, { en: () => import("./assistant.en") });
