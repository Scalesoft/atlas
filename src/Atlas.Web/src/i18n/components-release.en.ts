import { plural } from ".";
import type { ReleaseMessages } from "./components-release";

const en: ReleaseMessages = {
  changelog: {
    title: "What's new",
    description: "Major changes to Atlas since version 3.0, newest first.",
    back: "Back to Atlas",
  },
  cover: {
    aria: (label) => `${label}: wipe away the cover by dragging, or press Enter or Space`,
  },
  entry: {
    replay: (codename) => `Replay the intro of version ${codename}`,
    play: "Replay intro",
    current: "Current version",
    whatsNewIn: (label, codename) => `What's new in ${label}${codename ? ` ${codename}` : ""}`,
    patchesOf: (label) => `Patch versions of ${label}`,
  },
  splash: {
    markLabel: (label, codename) => `Mark of version ${label} ${codename}`.trim(),
    patchVersion: (version) => `Patch version ${version}`,
    newVersion: "New version",
    version: "Version",
    newMajorVersion: "New major version",
    majorVersion: "Major version",
    more: (count: number) => `And ${count} more`,
    patchLead: "Small fixes to the version you already know.",
    sincePatches: (since, count) => `Since your last version ${since}: ${plural(count, { one: "# patch version", other: "# patch versions" })}.`,
    sinceReleases: (since, count) => `What's new since your last version ${since}: ${plural(count, { one: "# release", other: "# releases" })}.`,
    fixesIn: (version) => `Fixes in Atlas ${version}`,
    mainlyIn: (label) => `Mainly in ${label}`,
    alsoIn: (range) => `Also in ${range}:`,
    continue: "Continue to Atlas",
    changelog: "Full changelog",
    hint: "Enter continues · Esc closes · click skips the intro",
  },
  demos: {
    pointers: { title: "Team meeting", budget: "Budget: ", approved: "approved ✓", launch: "Launch: ", before: "in March", after: "on Friday", next: "Next steps" },
    agent: { title: "Meeting notes", asked: "Sum up the meeting in one sentence.", summary: "The budget is approved, we launch on Friday.", name: "Assistant" },
    qr: { mention: "Jana Nováková", role: "Head of operations", scan: "Scan with your phone" },
    graph: { question: (topic) => `Who knows ${topic}?`, topics: ["PostgreSQL", "Vue", "Accounting"], people: ["Jana", "Anna", "Petr", "Marek"] },
  },
};
export default en;
