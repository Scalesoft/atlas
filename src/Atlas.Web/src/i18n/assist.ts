import { plural, translate } from ".";

/**
 * Suggestions while writing and the AI actions of the editor (assist/): titles, descriptions, reasons and notices of the rule-based
 * suggestions, the writing actions, the intents' names. Loaded with the assist module (lazy). The prompts and the model's intent
 * descriptions stay Czech in the code. The Czech source.
 */
const cs = {
  catalog: {
    "contact.inline": "Kontakt v textu",
    "contact.card": "Karta kontaktu",
    "page.link": "Odkaz na stránku",
    "youtrack.issue": "Úkol YouTrack",
    "secret.reference": "Heslo z trezoru",
    "secret.plaintext": "Možné heslo v textu",
    "table.fromText": "Tabulka",
    "tasklist.fromText": "Checklist",
    "callout.wrap": "Informační box",
    "code.fromText": "Blok kódu",
    "diagram.fromText": "Diagram",
    "toc.insert": "Obsah dokumentu",
    "chart.fromTable": "Graf z tabulky",
    "math.inline": "Vzorec",
  } as Record<string, string>,
  suggestionApplied: "Návrh použit",
  stale: "Návrh už neodpovídá textu.",
  youtrackUnverified: {
    title: (key: string) => `Úkol ${key} se nepodařilo ověřit`,
    description: "Zkontrolujte klíč, nastavení integrace nebo oprávnění.",
    reason: "Rozpoznaný klíč zatím nemá potvrzený viditelný úkol v YouTracku.",
  },
  plaintext: {
    title: "Možné heslo v textu",
    description: "Detekce běží jen ve vašem prohlížeči, modelu se nic neposílá.",
    apply: "Přesunout do trezoru",
    reason: "Řetězec za slovem jako „heslo:“ nebo „token:“, známý formát klíče nebo náhodně vypadající text. Pravidlo běží jen ve vašem prohlížeči.",
    example: "Je to jen příklad",
  },
  issue: {
    title: (key: string) => `Nahradit ${key} úkolem z YouTracku`,
    reason: (key: string) => `Klíč ${key} patří úkolu, který v YouTracku vidíte.`,
    done: "Úkol vložen",
  },
  page: {
    title: (title: string) => `Nahradit adresu odkazem na stránku „${title}“`,
    description: "Odkaz ukáže aktuální název stránky",
    reason: "Vložená adresa vede na stránku wiki, kterou smíte otevřít.",
    done: "Odkaz vložen",
  },
  contact: {
    reason: "V textu je jméno nebo e-mail osoby z adresáře wiki po slově jako „kontaktujte“ nebo „obraťte se na“.",
    emailTitle: "Vložit e-mailový odkaz",
    emailDescription: "Adresa není v adresáři; odkaz otevře poštovní aplikaci.",
    emailReason: "V textu je e-mailová adresa po výzvě ke kontaktu.",
    emailDone: "E-mailový odkaz vložen",
    pickTitle: "Vložit kontakt — vybrat z adresáře…",
    pickDescription: (query: string) => `Jménu „${query}“ odpovídá víc lidí`,
    replaceTitle: (written: string) => `Nahradit „${written}“ kontaktem`,
    cardBelowTitle: "Vložit kartu kontaktu pod odstavec",
    done: "Kontakt vložen",
    convertTitle: "Převést odstavec na kartu kontaktu",
    convertReason: "Odstavec obsahuje aspoň dva z údajů jméno, e-mail, telefon a pozice.",
  },
  secret: {
    title: "Vložit odkaz na heslo z trezoru…",
    description: (query: string) => `Hledat „${query}“ v trezorech`,
    reason: "Text mluví o hesle uloženém v trezoru; odkaz ukáže heslo jen tomu, kdo k němu smí.",
    done: "Odkaz na heslo vložen",
  },
  table: {
    title: "Převést na tabulku",
    description: (rows: number) => `${plural(rows, { one: "# řádek", few: "# řádky", other: "# řádků" })} · náhled před použitím`,
    reason: "Aspoň tři řádky mají stejný počet oddělovačů (tabulátor, středník nebo svislá čára).",
    done: "Tabulka vložena",
  },
  tasklist: {
    title: "Převést na checklist",
    reason: "Aspoň dva řádky začínají „[ ]“, „TODO“ nebo „úkol:“.",
    done: "Checklist vložen",
  },
  callout: {
    title: "Zabalit do informačního boxu",
    reason: (word: string) => `Odstavec začíná slovem „${word}“ nebo podobným.`,
    done: "Informační box vložen",
  },
  code: {
    title: "Převést na blok kódu",
    description: (language: string | null) => language ? `Jazyk ${language}` : "Bez zvýraznění",
    reason: "Aspoň tři řádky vypadají jako kód (závorky, středníky, odsazení, klíčová slova) nebo text začíná shebangem.",
    done: "Blok kódu vložen",
  },
  diagram: {
    title: "Vytvořit diagram",
    description: "Náhled diagramu před použitím",
    reason: "Text popisuje kroky spojené šipkami („A -> B -> C“) nebo je zápisem Mermaid.",
    done: "Diagram vložen",
  },
  math: {
    title: (span: string) => `Nahradit „${span}“ vzorcem`,
    reason: "Úsek je zapsaný jako vzorec.",
    done: "Vzorec vložen",
  },
  toc: {
    title: "Vložit obsah dokumentu na začátek",
    description: (count: number) => plural(count, { one: "# nadpis druhé úrovně", few: "# nadpisy druhé úrovně", other: "# nadpisů druhé úrovně" }),
    reason: "Dokument má aspoň šest nadpisů druhé úrovně a zatím žádný obsah.",
    done: "Obsah dokumentu vložen",
  },
  link: {
    alias: (phrase: string, title: string) => `„${phrase}“ je jiný název stránky „${title}“, kterou smíte číst.`,
    meaning: (phrase: string, title: string) => `Název stránky „${title}“ odpovídá zmínce „${phrase}“ významem.`,
    name: (phrase: string) => `„${phrase}“ je název stránky, kterou smíte číst.`,
    title: (phrase: string, title: string) => `Propojit „${phrase}“ se stránkou „${title}“`,
    description: (space: string) => `Ponechat text jako odkaz · ${space}`,
    done: "Text propojen se stránkou",
    replaceTitle: "Nahradit názvem stránky",
    replaceDescription: (title: string) => `„${title}“ · sleduje přejmenování`,
    replaceDone: "Odkaz na stránku vložen",
  },
  writing: {
    labels: {
      proofread: "Opravit pravopis a gramatiku", shorten: "Zkrátit", simplify: "Zjednodušit", formal: "Formálněji", informal: "Neformálněji",
      "translate-en": "Přeložit do angličtiny", "translate-cs": "Přeložit do češtiny", bullets: "Převést na odrážky", steps: "Převést na číslované kroky",
      table: "Převést na tabulku", custom: "Vlastní pokyn…",
    } as Record<string, string>,
    prepare: "Připravit návrh",
    cancel: "Zrušit",
    secretInInstruction: "Tajný údaj do modelového pokynu nepatří.",
    customPrompt: "Co se má s textem stát?",
    applied: "Úprava použita",
    outline: "Začít s osnovou",
    outlinePrompt: "O čem stránka bude?",
    outlineApplied: "Osnova použita",
  },
};

export type AssistMessages = typeof cs;
export default translate(cs, { en: () => import("./assist.en") });
