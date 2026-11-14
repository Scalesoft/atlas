import type { SettingsSetupMessages } from "./settings-setup";

const en: SettingsSetupMessages = {
  backupImport: { title: "Restore from backup", description: "Add content or restore archived pages" },
  modules: "Module settings",
  eyebrow: "ATLAS · YOUR WIKI IS READY",
  heading: "Connect your tools",
  intro: "Choose what you want to connect. You can leave everything for later.",
  completed: "The wiki and its admin are created",
  opening: "Opening…",
  openWiki: "Open wiki",
  modulesHeading: "Modules and integrations",
  loading: "Loading modules…",
  navigation: "Settings of individual modules",
  formMissingTitle: "The module form isn't available",
  formMissingText: "This version of the client doesn't know the module form. Refresh the page or check the deployed version.",
  refresh: "Refresh page",
  dirty: "Save or discard your unsaved changes before moving to the next module.",
  back: "Back",
  step: (position, total) => `Step ${position} of ${total} · optional`,
  ready: "Ready",
  later: "Any time later in Wiki administration",
  finishing: "Finishing…",
  nextModule: "Next option",
  emptyTitle: "No modules to set up",
  emptyText: "This installation has no module that can be set up.",
  loadFailed: "Couldn't load the modules",
  reload: "Reload",
  adminOnlyTitle: "Wiki admins only",
  adminOnlyText: "Modules and integrations are set up by a wiki admin.",
};

export default en;
