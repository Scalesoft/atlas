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
    verify: {
      title: "Server outage procedure", steward: "Owner", stewardName: "Jana Nováková", until: (date) => `valid until ${date}`, dates: ["2/25/2027", "5/25/2027"],
      verified: "Verified", expiring: "Expires in 14 days", expired: "Verification expired", reminder: "Expires in 14 days: a reminder to its owner", verify: "Verify", today: "verified today by Jana",
    },
    health: {
      tiles: ["Verified", "Expires soon", "Expired", "No owner"], counts: [42, 5, 3, 2], readers: (count) => `${count} readers`,
      pages: [{ title: "Leave and holidays", state: "verified", readers: 38 }, { title: "Server outage procedure", state: "expired", readers: 214 }, { title: "New starters", state: "expiring", readers: 96 }],
      states: { verified: "Verified", expiring: "Expires soon", expired: "Expired" },
    },
    discussion: { title: "Launch plan", name: "Jana", text: "I added the suppliers' contacts.", reply: "Reply", answer: "Thanks, I'll have a look.", send: "Send", dock: "Discussion" },
    color: { title: "My colour", auto: "Automatic", you: "You", other: "Petr", line: "Launch on Friday" },
  },
};
export default en;
