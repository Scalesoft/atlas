import { translate } from ".";

/** Secrets in the editor: what the host says when a release fails, the slash-menu entry, the move to the vault. Small, because the host is eager. The Czech source. */
const cs = {
  aborted: "Operace byla zrušena.",
  reauthUnsupported: "Heslo vyžaduje nové přihlášení, které váš poskytovatel přihlášení zatím neumí prokázat.",
  reauthFailed: "Nové přihlášení se nepodařilo ověřit.",
  unavailable: "Heslo není dostupné.",
  fieldNotCopyable: "Toto pole nelze zkopírovat.",
  fieldEmpty: "Pole je v hesle prázdné.",
  previewNoAccess: "Z tohoto náhledu nelze žádat o přístup.",
  previewNoReveal: "Tento náhled hesla nezobrazuje.",
  previewNoCopy: "Tento náhled hesla nekopíruje.",
  reauthBusy: "Právě probíhá jiné ověření.",
  reauthCanceled: "Ověření bylo zrušeno.",
  staleProposal: "Návrh už neodpovídá textu.",
  moved: (vault?: string) => vault ? `Heslo je v trezoru ${vault}. V textu zůstal jen odkaz.` : "Heslo je v trezoru. V textu zůstal jen odkaz.",
  action: { label: "Heslo z trezoru", description: "Odkaz na heslo, které smíte použít", keywords: ["heslo", "password", "trezor", "keepass", "přihlašovací údaje"] },
};

export type SecretsMessages = typeof cs;
export default translate(cs, { en: () => import("./secrets.en") });
