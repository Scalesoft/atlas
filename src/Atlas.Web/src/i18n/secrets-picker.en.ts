import type { SecretsPickerMessages } from "./secrets-picker";

const en: SecretsPickerMessages = {
  picker: {
    pick: "Insert secret",
    replace: "Move to vault",
    swap: "Replace secret",
    hintReplace: "Save the secret in a vault (KeePass) and pick it here. The text in the document is replaced by a reference.",
    hintSwap: "Pick another secret. The reference keeps its display; only the reference is saved in the page.",
    hintPick: "Only a reference is saved in the page. Only people who may use the secret can see its value.",
    layout: "Display",
    block: "Card",
    inline: "In text",
    untitled: "Untitled",
    list: "Secrets",
    search: "Search by name or path in KeePass…",
    searching: "Searching…",
    none: "No secret you may use matches the search.",
  },
  reauth: {
    titleCopy: "Verification before copying the secret",
    titleReveal: "Verification before showing the secret",
    heading: "Confirm with a fresh sign-in",
    description: "This secret is protected by repeated sign-in. Releasing the value is recorded in the audit log.",
    confirmCopy: "Copy secret",
    confirmReveal: "Show secret",
  },
};

export default en;
