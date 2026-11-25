import type { ProfileMessages } from "./profile";

const en: ProfileMessages = {
  appearance: {
    title: "Appearance",
    save: "Save appearance",
    on: "On",
    off: "Off",
    language: {
      label: "Language",
      help: "The language of menus, dialogs and messages, also on the sign-in and shared pages. Page content is not translated. Atlas reloads once you save.",
      auto: "Automatic",
      autoResult: (name) => `Assigned colour: ${name}`,
      cs: "Čeština",
      en: "English",
    },
    collaboratorColor: {
      label: "My colour",
      help: "The colour of your caret, avatar and pointer when others work on the page or board with you. If your colour is taken by someone present, Atlas gives you the nearest free one; whoever came first keeps theirs.",
      auto: "Automatic",
      custom: "Custom",
      swatches: "Caret and avatar colour",
    },
    mode: { label: "Theme", help: "Applies to you only, on all your devices.", light: "Light", dark: "Dark", system: "Automatic" },
    accent: { label: "Accent color", help: "The dark theme is gently tinted towards it. In the light theme it colors buttons, links and selection.", default: "default" },
    layout: {
      label: "Layout",
      help: "How navigation and content share the window.",
      connected: "Connected",
      connectedHint: "Navigation and content on one surface",
      islands: "Islands",
      islandsHint: "Separate panels with a margin",
    },
    reading: {
      label: "Reading width",
      help: "The line length of documents. This browser remembers it; in a document you can also switch it under More actions.",
      comfortable: "Comfortable",
      wide: "Wide",
    },
    motion: {
      label: "Reduce motion",
      help: "No transitions or moving content. Follows the system by default.",
      system: (reduced) => `Automatic (${reduced ? "on" : "off"})`,
    },
    openMode: {
      label: "Open pages",
      help: "Read-only: pages and boards open locked, and you edit them with Edit at the top right (or E). Applies on all your devices.",
      edit: "Ready to edit",
      read: "Read-only",
    },
    dragPages: {
      label: "Move pages by dragging",
      help: "Pages in the space tree can be dragged elsewhere. Turned off, they never move by accident; move them with Move… or Alt+arrows.",
    },
  },
  otherSessions: {
    title: "Other devices",
    description: "Sign-ins in other browsers and on other devices.",
    action: "Sign out other devices",
    help: "Use this when you have lost a device or signed in on someone else's computer. This sign-in stays.",
    confirmTitle: "Sign out other devices?",
    confirm: "Sign out others",
    confirmText: "The sign-ins on all other devices and in all other browsers end. Your personal API tokens end too, as they are bound to the sign-in. This sign-in stays.",
    done: "The other sign-ins have ended. Sign in again on the other devices and issue new personal API tokens.",
  },
};
export default en;
