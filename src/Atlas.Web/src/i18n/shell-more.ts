import { plural, translate } from ".";

/** Shell texts the first screen does not need (upload, trash, OpenAPI, Hub, vault and protection notes, API levels, suggestions, toolbar), kept out of shell.ts which loads eagerly. The Czech source. */
const cs = {
  upload: {
    needWrite: "Pro nahrání souboru musíte mít právo upravit uloženou stránku.",
    tooLarge: (mib: number) => `Soubor může mít nejvýše ${mib} MiB.`,
    changed: "Stránka nebo oprávnění se během nahrávání změnila. Soubor nebyl vložen do obsahu.",
  },
  trash: {
    daysProblem: (min: number, max: number) => `Zadejte celý počet dní od ${min} do ${max}.`,
  },
  src: {
    loadFailed: (reason: string) => `Editor zdroje se nepodařilo načíst: ${reason}`,
  },
  openapi: {
    checking: "Kontrola YAML se ještě načítá. Zkuste Použít znovu.",
    stale: "Definice se mezitím v dokumentu změnila. Zkopírujte nebo stáhněte svůj návrh, případně jej zahoďte a načtěte aktuální verzi.",
    unavailable: "Změnu teď nelze použít do stránky. Rozpracovaný YAML zůstává zachovaný.",
    lost: "Rozpracovaný YAML už nejde použít: blok OpenAPI mohl odstranit jiný autor, nebo se dokument znovu načetl. Návrh zůstává zachovaný v tomto okně; můžete si jej stáhnout.",
    readonly: "Oprávnění upravovat se změnilo. Rozpracovaný YAML zůstává zachovaný v tomto okně; můžete si jej stáhnout.",
    detached: "Dokument se znovu načetl. Dokumentace ukazuje definici z okamžiku otevření; aktuální verzi zobrazíte po zavření a novém otevření detailu.",
  },
  hub: {
    disabled: "Vypnuto",
    preview: "Náhled",
    enforced: "Uplatňovat přístupy",
    timeout: "Hub neodpověděl včas.",
    unavailable: "Hub je dočasně nedostupný.",
    unauthorized: "Hub odmítl čtecí token. Správce musí ověřit připojení.",
    forbidden: "Čtecí účet nemá potřebná oprávnění v Hubu.",
    rateLimited: "Hub dočasně omezuje počet požadavků. Zkuste ověření později.",
    notVisible: "Účet nebo skupina není čtecímu účtu dostupná.",
    identityMismatch: "Hub vrátil jinou identitu. Obraťte se na správce wiki.",
    invalidResponse: "Odpověď Hubu nebylo možné ověřit.",
    responseTooLarge: "Odpověď Hubu překročila podporovanou velikost.",
    verificationFailed: "Ověření se nezdařilo. Pokud potíže přetrvají, obraťte se na správce wiki.",
  },
  vault: {
    managed: {
      label: "Na serveru wiki",
      description: "O složku se postará Atlas, nic dalšího nezadáváte",
      badge: "Na serveru",
    },
    local: {
      label: "Vlastní složka na serveru",
      description: "Absolutní cesta na disku serveru, mimo publikovaný obsah",
      badge: "Vlastní složka",
    },
    smb: {
      label: "Sdílená složka SMB",
      description: "Síťová složka, kterou server otevírá vlastním účtem služby",
    },
    status: {
      unlocked: "Odemčeno",
      needsPassword: "Vyžaduje heslo",
      unavailable: "Nedostupné",
      error: "Chyba",
    },
    field: {
      userName: "Uživatel",
      password: "Heslo",
      url: "Adresa",
      notes: "Poznámky",
    },
  },
  protection: {
    never: "Nevyžadovat",
    outside: "Mimo důvěryhodné sítě",
    always: "Vždy",
  },
  cover: {
    all: "Vše",
    company: "Firma",
    field: "Obor",
    general: "Obecné",
  },
  apilevel: {
    none: {
      label: "Bez API",
      description: "Osobní API tokeny nelze vydávat ani používat.",
    },
    read: {
      label: "Čtení",
      description: "Čte prostory a stránky, hledá.",
      verb: "Čte",
    },
    write: {
      label: "Zápis",
      description: "Čte i mění obsah jako člověk; co uvidí čtenáři, určuje publikace stránky.",
      verb: "Zapisuje",
    },
  },
  channel: {
    mcp: "přes MCP",
    api: "přes API",
    assistant: "přes asistenta",
    system: "systém",
    token: (name: string, revoked: boolean) => `token ‚${name}‘${revoked ? " (odvolaný)" : ""}`,
  },
  suggest: {
    contact: {
      label: "Kontakty",
      description: "Jméno kolegy se změní na kontakt.",
    },
    pageLink: {
      label: "Odkazy na stránky",
      description: "Název stránky se změní na odkaz.",
    },
    issue: {
      label: "Úkoly",
      description: "Klíč úkolu se změní na komponentu z YouTracku.",
    },
    structure: {
      label: "Převody struktury",
      description: "Odrážky, tabulky a nadpisy z psaného textu.",
    },
    mention: {
      label: "Propojení zmínek",
      description: "Zmínka stránky v textu se propojí.",
    },
  },
  code: {
    code: "Kód",
    copyLanguage: (language: string) => `Kopírovat kód ${language}`,
    copyCode: "Kopírovat kód",
    copy: "Kopírovat",
    copied: "Zkopírováno",
    notCopied: "Nezkopírováno",
    copiedStatus: "Kód zkopírován",
    copyFailedStatus: "Kód se nepodařilo zkopírovat; označte jej a zkopírujte ručně.",
  },
  tb: {
    undo: "Zpět",
    bold: "Tučně",
    italic: "Kurzíva",
    strike: "Přeškrtnutí",
    code: "Kód v textu",
    bullet: "Odrážky",
    ordered: "Číslovaný seznam",
    task: "Úkoly",
    timed: "Časová odrážka",
    quote: "Citace",
    callout: "Informační box",
    table: "Tabulka",
    paragraph: "Odstavec",
    heading1: "Nadpis 1",
    heading2: "Nadpis 2",
    heading3: "Nadpis 3",
    heading4: "Nadpis 4",
    codeBlock: "Blok kódu",
    formatting: "Formátování",
    blockType: "Typ bloku",
    blockTypeTitle: (label: string) => `Typ bloku: ${label}`,
    selected: "(vybráno)",
  },
  outline: {
    sections: (count: number) => plural(count, { one: "# sekce", few: "# sekce", other: "# sekcí" }),
  },
  keyboardHelp: {
    title: "Klávesové zkratky",
    then: "pak",
    noteBefore: "Písmenové zkratky platí mimo textová pole a editor. Všechny akce najdete také v hledání: ",
    noteAnd: " a znak ",
    noteEnd: ".",
  },
  doc: {
    noPermalink: "Stránka nemá trvalou adresu.",
    findMentions: "Kde se to ještě zmiňuje",
    copySectionLink: "Kopírovat odkaz na sekci",
    schemaMismatch: "Wiki mezitím dostala novější verzi editoru. Obnovte stránku a pokračujte v úpravách; obsah můžete zatím dál číst.",
    technicalDetails: "Technické podrobnosti",
    refreshPage: "Obnovit stránku",
    reconnect: "Připojit znovu",
    openFailed: (reason: string) => `Dokument se nepodařilo otevřít: ${reason} Uložený obsah nebyl změněn.`,
    pageView: "Zobrazení stránky",
    pageContent: "Obsah stránky",
    draftChanges: "Rozpracované změny zdroje",
    sourceUnsaved: "Zdroj stránky obsahuje neuložené změny.",
    sourceUnsavedExcluded: "Zdroj stránky obsahuje neuložené změny. Tato akce je nezahrne; návrh můžete zahodit, nebo se k němu vrátit.",
    applyAndClose: "Použít a zavřít",
    documentTab: "Dokument",
    sourceTab: "Zdroj",
    pageSource: "Zdroj stránky",
  },
  role: {
    discover: "Vidí",
  },
  diff: {
    added: "přidáno",
    removed: "odstraněno",
    sourceChanged: "zdroj změněn",
    settingsChanged: "nastavení změněno",
  },
  fold: {
    expand: "Rozbalit sekci",
    collapse: "Sbalit sekci",
    named: (action: string, title: string) => `${action}: ${title}`,
    heading: "nadpis",
    hint: (action: string, key: string) => `${action} (Alt+${key})`,
  },
  kb: {
    typingMarkdown: "Psaní Markdownu",
    typedAtLineStart: (label: string) => `${label} (napsat na začátek řádku)`,
  },
};
export type ShellMoreMessages = typeof cs;

export default translate(cs, { en: () => import("./shell-more.en") });
