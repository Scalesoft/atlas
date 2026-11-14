import { plural, translate } from ".";

/** The AI core words, small because the eager code needs them: unavailable and limit sentences, the status line, the effort steps, the source count. The answer card and the rest are in ai-answer.ts. The Czech source. */
const cs = {
  unavailable: "Tahle funkce teď není k dispozici.",
  limit: "Limit využití asistenta byl pro dnešek dosažen.",
  sources: (count: number) => plural(count, { one: "# zdroj", few: "# zdroje", other: "# zdrojů" }),
  status: {
    readPage: "Čtu stránku",
    searchSpace: (name?: string | null) => name ? `Hledám v prostoru ${name}` : "Hledám v prostoru",
    searchWiki: "Hledám ve wiki",
    writeSummary: "Píšu shrnutí",
    writeAnswer: "Píšu odpověď",
    stop: "Zastavit",
    progress: "Průběh",
  },
};

export type AiMessages = typeof cs;
export default translate(cs, { en: () => import("./ai.en") });
