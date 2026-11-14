import type { AiQuotaMessages } from "./ai-quota";

const en: AiQuotaMessages = {
  quota: {
    tasks: { "search.answer": "Answers in search", "chat.answer": "Assistant", "page.summary": "Page summaries", "assistant.agent": "Assistant with tools" },
    user: "Personal limit",
    group: (name) => `Group ${name}`.trim(),
    wikiDefault: "Wiki default limit",
    remaining: (label, remaining, total) => `${label}: ${remaining} of ${total} left`,
    remainingShort: (remaining, total) => `${remaining} of ${total} left`,
    resets: (when) => `Resets ${when}`,
    today: "AI usage today",
    failed: "Couldn't load today's usage.",
    disabled: "You don't have access to the AI model features.",
    noLimit: "No daily limit",
    usedToday: (count) => `Used today ${count}`,
  },
  selection: {
    toolbar: "Text selection actions",
    copy: "Copy",
    copied: "Copied",
    copyFailed: "Couldn't copy the text.",
    section: (name) => `Section “${name}”`,
    about: (verb, section) => section ? `${verb} section “${section}”` : `${verb} the selected text`,
    askVerb: "Ask the assistant about",
    explainVerb: "Explain",
    summarizeVerb: "Summarize",
    ask: "Ask",
    explain: "Explain",
    summarize: "Summarize",
    copySectionLink: "Copy section link",
    find: "Where else it's mentioned",
  },
};
export default en;
