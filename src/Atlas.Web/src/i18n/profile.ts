import { translate } from ".";

/** Profile: Vzhled (ProfilePage, profile/AccentPicker) and Ostatní zařízení (profile/OtherSessions). The Czech source. */
const cs = {
  appearance: {
    title: "Vzhled",
    save: "Uložit vzhled",
    on: "Zapnuto",
    off: "Vypnuto",
    language: {
      label: "Jazyk",
      help: "Jazyk nabídek, dialogů a hlášek, také na přihlášení a sdílených stránkách. Obsah stránek se nepřekládá. Po uložení se Atlas znovu načte.",
      auto: "Automaticky",
      // A language is named in itself, in every dictionary.
      cs: "Čeština",
      en: "English",
    },
    collaboratorColor: {
      label: "Moje barva",
      help: "Barva vašeho kurzoru, avataru a ukazatele, když s vámi na stránce nebo tabuli pracují další. Je-li vaše barva mezi přítomnými obsazená, Atlas vám přidělí nejbližší volnou; kdo přišel dřív, svou barvu si ponechá.",
      auto: "Automaticky",
      custom: "Vlastní",
      swatches: "Barva kurzoru a avataru",
    },
    mode: { label: "Motiv", help: "Platí jen pro vás, na všech zařízeních.", light: "Světlý", dark: "Tmavý", system: "Podle systému" },
    accent: { label: "Barva zvýraznění", help: "Do ní se jemně tónuje tmavý motiv. Ve světlém mění tlačítka, odkazy a výběr.", default: "výchozí" },
    layout: {
      label: "Rozložení",
      help: "Jak se navigace a obsah dělí o okno.",
      connected: "Propojené",
      connectedHint: "Navigace a obsah na jedné ploše",
      islands: "Ostrovy",
      islandsHint: "Oddělené panely s okrajem",
    },
    reading: {
      label: "Šířka čtení",
      help: "Délka řádku v dokumentech. Pamatuje si ji tento prohlížeč; u dokumentu ji přepnete i v nabídce Další akce.",
      comfortable: "Pohodlná",
      wide: "Široká",
    },
    motion: {
      label: "Omezit animace",
      help: "Bez přechodů a pohybu obsahu. Výchozí podle systému.",
      /** Off follows the device, so the row says what the device asks for. */
      system: (reduced: boolean) => `Podle systému (${reduced ? "zapnuto" : "vypnuto"})`,
    },
    openMode: {
      label: "Stránky otevírat",
      help: "Jen pro čtení: stránky a tabule se otevřou zamčené a upravíte je tlačítkem Upravit vpravo nahoře (nebo E). Platí na všech vašich zařízeních.",
      edit: "Rovnou k úpravám",
      read: "Jen pro čtení",
    },
    dragPages: {
      label: "Přesouvat stránky tažením",
      help: "Stránky ve stromu prostoru jdou přetáhnout jinam. Vypnuté se nepřesunou omylem; přesouvají se přes nabídku Přesunout… nebo Alt+šipkami.",
    },
  },
  otherSessions: {
    title: "Ostatní zařízení",
    description: "Přihlášení v jiných prohlížečích a na jiných zařízeních.",
    action: "Odhlásit ostatní zařízení",
    help: "Použijte, když jste ztratili zařízení nebo se přihlásili na cizím počítači. Toto přihlášení zůstane.",
    confirmTitle: "Odhlásit ostatní zařízení?",
    confirm: "Odhlásit ostatní",
    confirmText: "Přihlášení na všech ostatních zařízeních a v ostatních prohlížečích skončí. Skončí i vaše osobní API tokeny, protože jsou vázané na přihlášení. Toto přihlášení zůstane.",
    done: "Ostatní přihlášení skončila. Na ostatních zařízeních se přihlásíte znovu; osobní API tokeny vydejte nové.",
  },
};
export type ProfileMessages = typeof cs;

export default translate(cs, { en: () => import("./profile.en") });
