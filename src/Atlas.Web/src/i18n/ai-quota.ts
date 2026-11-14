import { translate } from ".";

/** The quota line and the read page's selection bar (ai/AiQuotaStatus, ReadonlySelectionActions), small because the shell shows them at once; the answer card and the rest are in ai-answer. The Czech source. */
const cs = {
  quota: {
    tasks: { "search.answer": "Odpovědi v hledání", "chat.answer": "Asistent", "page.summary": "Shrnutí stránek", "assistant.agent": "Asistent s nástroji" } as Record<string, string>,
    user: "Osobní limit",
    group: (name: string) => `Skupina ${name}`.trim(),
    wikiDefault: "Výchozí limit wiki",
    remaining: (label: string, remaining: number | null, total: number | null) => `${label}: zbývá ${remaining} z ${total}`,
    remainingShort: (remaining: number | null, total: number | null) => `Zbývá ${remaining} z ${total}`,
    resets: (when: string) => `Obnovení ${when}`,
    today: "Dnešní využití AI",
    failed: "Denní spotřebu se nepodařilo načíst.",
    disabled: "Nemáte povolený přístup k modelovým funkcím AI.",
    noLimit: "Bez denního limitu",
    usedToday: (count: number) => `Dnes použito ${count}`,
  },
  selection: {
    toolbar: "Akce výběru textu",
    copy: "Kopírovat",
    copied: "Zkopírováno",
    copyFailed: "Text se nepodařilo zkopírovat.",
    section: (name: string) => `Sekce „${name}“`,
    about: (verb: string, section?: string | null) => section ? `${verb} sekci „${section}“` : `${verb} vybraný text`,
    askVerb: "Zeptat se asistenta na",
    explainVerb: "Vysvětlit",
    summarizeVerb: "Shrnout",
    ask: "Zeptat se",
    explain: "Vysvětlit",
    summarize: "Shrnout",
    copySectionLink: "Kopírovat odkaz na sekci",
    find: "Kde se to ještě zmiňuje",
  },
};
export type AiQuotaMessages = typeof cs;

export default translate(cs, { en: () => import("./ai-quota.en") });
