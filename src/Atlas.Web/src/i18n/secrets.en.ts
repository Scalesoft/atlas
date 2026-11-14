import type { SecretsMessages } from "./secrets";

const en: SecretsMessages = {
  aborted: "The operation was canceled.",
  reauthUnsupported: "This secret requires a fresh sign-in, which your sign-in provider can't prove yet.",
  reauthFailed: "Couldn't verify the new sign-in.",
  unavailable: "The secret isn't available.",
  fieldNotCopyable: "This field can't be copied.",
  fieldEmpty: "The field in the secret is empty.",
  previewNoAccess: "Access can't be requested from this preview.",
  previewNoReveal: "This preview doesn't show the secret.",
  previewNoCopy: "This preview doesn't copy the secret.",
  reauthBusy: "Another verification is already in progress.",
  reauthCanceled: "The verification was canceled.",
  staleProposal: "The suggestion no longer matches the text.",
  moved: (vault) => vault ? `The secret is in vault ${vault}. Only a reference stays in the text.` : "The secret is in the vault. Only a reference stays in the text.",
  action: { label: "Secret from the vault", description: "A reference to a secret you may use", keywords: ["secret", "password", "vault", "keepass", "credentials"] },
};

export default en;
