/**
 * Context intent catalogue, version 1 (design spec, appendix A). Data only: the detectors live in intentRules.ts and
 * secretDetector.ts, the editor commands and resolvers in the `atlasAssist` plugin (H2).
 */
export const intentCatalogVersion = 1;

export type IntentId =
  | "contact.inline"
  | "contact.card"
  | "page.link"
  | "youtrack.issue"
  | "secret.reference"
  | "secret.plaintext"
  | "table.fromText"
  | "tasklist.fromText"
  | "callout.wrap"
  | "code.fromText"
  | "diagram.fromText"
  | "toc.insert"
  | "chart.fromTable"
  | "math.inline";

/** How applying the suggestion changes the document. */
export type IntentVariant = "replace-span" | "convert-block" | "insert-after" | "insert-start";

/** Names from the editor icon registry (`@atlas/markdown/icons`); a rule suggestion shows its type, never the AI glyph (plan §0a). */
export type IntentIcon =
  | "user" | "users" | "link" | "ticket" | "key-round" | "triangle-alert" | "table" | "list-checks"
  | "callout" | "square-code" | "workflow" | "outline" | "bar-chart-3" | "sigma";

export interface IntentDefinition {
  readonly id: IntentId;
  // The name in the UI (appendix A) is i18n/assist.ts `catalog`: the rules module carries no texts of the tool.
  readonly icon: IntentIcon;
  readonly variant: IntentVariant;
  /** Whether a model may propose the intent (phase 4). `secret.plaintext` never: the block never reaches a model. */
  readonly modelAllowed: boolean;
  /** Detected by the phase 1 rules. */
  readonly phase1: boolean;
  /** What a model sees about the intent: the ID and this description, never people, pages or secrets. */
  readonly description?: string;
}

export const intentCatalog: readonly IntentDefinition[] = [
  {
    id: "contact.inline", icon: "user", variant: "replace-span", modelAllowed: true, phase1: true,
    description: "Zmínka konkrétní osoby, na kterou se čtenář má obrátit (jméno, případně oddělení).",
  },
  {
    id: "contact.card", icon: "users", variant: "convert-block", modelAllowed: true, phase1: true,
    description: "Blok s kontaktními údaji osoby nebo firmy (jméno, e-mail, telefon, pozice).",
  },
  { id: "page.link", icon: "link", variant: "replace-span", modelAllowed: false, phase1: true },
  { id: "youtrack.issue", icon: "ticket", variant: "replace-span", modelAllowed: false, phase1: true },
  {
    id: "secret.reference", icon: "key-round", variant: "insert-after", modelAllowed: true, phase1: true,
    description: "Text odkazuje na heslo nebo přihlašovací údaje uložené jinde (trezor).",
  },
  { id: "secret.plaintext", icon: "triangle-alert", variant: "replace-span", modelAllowed: false, phase1: true },
  { id: "table.fromText", icon: "table", variant: "convert-block", modelAllowed: false, phase1: true },
  { id: "tasklist.fromText", icon: "list-checks", variant: "convert-block", modelAllowed: false, phase1: true },
  {
    id: "callout.wrap", icon: "callout", variant: "convert-block", modelAllowed: true, phase1: true,
    description: "Odstavec je výslovné varování před rizikem, důležité upozornění nebo tip, typicky uvozený slovem Upozornění, Pozor, Důležité nebo Tip. Běžný pokyn, pravidlo nebo popis stavu upozornění není.",
  },
  { id: "code.fromText", icon: "square-code", variant: "convert-block", modelAllowed: false, phase1: true },
  {
    id: "diagram.fromText", icon: "workflow", variant: "convert-block", modelAllowed: true, phase1: true,
    description: "Text popisuje proces nebo tok kroků vhodný pro diagram.",
  },
  { id: "toc.insert", icon: "outline", variant: "insert-start", modelAllowed: false, phase1: true },
  { id: "chart.fromTable", icon: "bar-chart-3", variant: "insert-after", modelAllowed: false, phase1: false },
  { id: "math.inline", icon: "sigma", variant: "replace-span", modelAllowed: false, phase1: false },
];

const byId = new Map(intentCatalog.map(intent => [intent.id, intent]));

export function isIntentId(value: string): value is IntentId {
  return byId.has(value as IntentId);
}

export function intentDefinition(id: IntentId): IntentDefinition {
  return byId.get(id)!;
}
