import { plural, translate } from ".";

/** The home dashboard (components/home): the landing after sign-in and its cards. The Czech source. */
const cs = {
  title: "Domů",
  intro: "Co se změnilo ve wiki a kde jste naposledy byli.",
  cards: "Přehled",
  showAll: "Zobrazit vše",
  showAllOf: (title: string) => `Zobrazit vše: ${title}`,
  retry: "Zkusit znovu",
  loadFailed: (title: string) => `${title} se nepodařilo načíst`,
  loading: (title: string) => `Načítání: ${title}…`,
  changes: {
    title: "Co se změnilo",
    filter: "Zobrazit změny",
    all: "Vše",
    ai: "Změny od AI",
    mine: "Moje stránky",
    emptyTitle: "Za posledních 30 dní se nic nezměnilo",
    emptyAi: "AI za posledních 30 dní nic nezměnila",
    emptyMine: "Na vašich stránkách se za posledních 30 dní nic nezměnilo",
    emptyHint: "Změny stránek, které smíte číst, se tu objeví hned.",
    more: "Načíst další",
    further: (count: number) => plural(count, { one: "+# další změna", few: "+# další změny", other: "+# dalších změn" }),
    openPage: (title: string) => `Otevřít stránku ${title}`,
    history: "Historie",
    openHistory: (title: string) => `Otevřít změnu stránky ${title} v historii`,
    aiMark: "AI",
    aiMarkTitle: "Změna s AI; autorem zůstává člověk, za kterého AI jednala.",
  },
  recent: {
    title: "Naposledy navštívené",
    emptyTitle: "Zatím jste nic neotevřeli",
    emptyHint: "Stránky, které otevřete, tu najdete znovu.",
    openPage: (title: string) => `Otevřít ${title}`,
  },
  waiting: {
    title: "Čeká na mě",
    unread: (count: number) => plural(count, { one: "# nepřečtené oznámení", few: "# nepřečtená oznámení", other: "# nepřečtených oznámení" }),
    noUnread: "Žádná nepřečtená oznámení",
    drafts: "Koncepty s nepublikovanými změnami",
    noDrafts: "Žádné rozpracované koncepty",
    openDraft: (title: string) => `Otevřít koncept ${title}`,
  },
};

export type HomeMessages = typeof cs;
export default translate(cs, { en: () => import("./components-home.en") });
