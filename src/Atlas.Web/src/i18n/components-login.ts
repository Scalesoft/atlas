import { translate } from ".";

/** Login page (auth/LoginLayout, auth/LoginMethods): shown to people without an account, so the file stays small. The Czech source. */
const cs = {
  art: { label: "Atlas — prostor pro znalosti", eyebrow: "prostor pro znalosti" },
  tagline: { title: "Znalosti ožívají.", text: "Souvislosti, které vás posunou dál." },
  label: "Přihlášení",
  loading: "Načítání přihlášení…",
  passwordMismatch: "Nová hesla se neshodují.",
  activated: "Heslo bylo nastaveno. Nyní se přihlaste svým uživatelským jménem.",
  badProviderUrl: "Přihlašovací služba má neplatnou adresu. Obraťte se na správce.",
  externalFailed: "Externí přihlášení se nezdařilo. Zkuste se přihlásit znovu, případně kontaktujte správce.",
  continueWith: (name: string) => `Pokračovat přes ${name}`,
  orLocal: "nebo místním účtem",
  username: "Uživatelské jméno",
  password: "Heslo",
  signingIn: "Přihlašujeme…",
  signIn: "Přihlásit se",
  noMethod: "Není zapnutá žádná metoda přihlášení. Obraťte se na správce serveru.",
  developer: "Přihlásit jako vývojář",
  developerHelp: "jen pro vývoj · bez hesla jako první správce wiki",
  activationLabel: "Aktivace účtu nebo obnova hesla",
  activationTitle: "Vlastní heslo pro váš účet",
  activationHelp: "Použijte jednorázový kód od správce. Po vypršení platnosti požádejte správce o nový.",
  activationCode: "Aktivační kód",
  newPassword: "Nové heslo",
  confirmPassword: "Potvrzení nového hesla",
  passwordRule: "Alespoň 12 znaků. Kód lze použít pouze jednou.",
  settingPassword: "Nastavujeme heslo…",
  setPassword: "Nastavit heslo",
  backToSignIn: "Zpět k přihlášení",
  activate: "Aktivovat účet / obnovit heslo",
  recover: "Obnovit přístup správce",
  reload: "Načíst znovu",
};
export type LoginMessages = typeof cs;

export default translate(cs, { en: () => import("./components-login.en") });
