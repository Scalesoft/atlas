import { translate } from ".";

/** The secret picker and the fresh sign-in dialog of a release (secrets/SecretPicker, SecretReauthDialog), loaded with them. The Czech source. */
const cs = {
  picker: {
    pick: "Vložit heslo",
    replace: "Přesunout do trezoru",
    swap: "Vyměnit heslo",
    hintReplace: "Uložte heslo do trezoru (KeePass) a vyberte ho. Text v dokumentu se nahradí odkazem.",
    hintSwap: "Vyberte jiné heslo. Odkaz si nechá své zobrazení; do stránky se uloží jen odkaz.",
    hintPick: "Do stránky se uloží jen odkaz. Hodnotu uvidí jen ten, kdo k heslu smí.",
    layout: "Zobrazení",
    block: "Karta",
    inline: "V textu",
    untitled: "Bez názvu",
    list: "Hesla",
    search: "Hledat název nebo cestu v KeePassu…",
    searching: "Hledáme…",
    none: "Žádné heslo, které smíte použít, neodpovídá hledání.",
  },
  reauth: {
    titleCopy: "Ověření před kopírováním hesla",
    titleReveal: "Ověření před zobrazením hesla",
    heading: "Potvrďte to novým přihlášením",
    description: "Toto heslo je chráněné opakovaným přihlášením. Vydání hodnoty se zapíše do auditu.",
    confirmCopy: "Zkopírovat heslo",
    confirmReveal: "Zobrazit heslo",
  },
};

export type SecretsPickerMessages = typeof cs;
export default translate(cs, { en: () => import("./secrets-picker.en") });
