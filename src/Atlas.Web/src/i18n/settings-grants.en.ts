import type { SettingsGrantsMessages } from "./settings-grants";

const en: SettingsGrantsMessages = {
  list: (items) => items.join(" and "),
  explanation: {
    label: (subject, target) => `Why ${subject} has access to ${target}`,
    until: (until) => `, until ${until}`,
    automatic: (subject, scope, level, label) => `${subject} automatically has level ${level} on ${scope} as ${label}.`,
    direct: (subject, level, scope, until) => `${subject} has a direct grant of level ${level} on ${scope}${until}.`,
    viaGroup: (subject, label, level, scope, until) => `${subject} has level ${level} on ${scope} through group ${label}${until}.`,
    none: (subject, target) => `${subject} has no access to ${target}.`,
    summary: (subject, level, includes, paths) => `${subject} has level ${level}${includes ? ` (includes ${includes})` : ""}${paths > 1 ? `, the highest of ${paths} paths` : ""}.`,
    automaticBadge: "Automatic",
    untilBadge: (until) => `until ${until}`,
  },
  level: {
    spoken: (label, includes) => (includes ? `Level ${label}, includes ${includes}` : `Level ${label}`),
    includes: (label, list) => `${label} also includes ${list}.`,
    lowest: (label) => `${label} is the lowest level.`,
  },
  effective: {
    label: "Applies",
    none: "Not set",
    own: (level) => `Own setting (${level})`,
    inherited: (level) => `Inherited from ${level}`,
    ownBadge: "Own",
    inheritedBadge: "Inherited",
    origin: "Where the value comes from",
    takes: "inherits",
    change: "Change",
    revert: "Revert to inherited",
    custom: "Set own value",
  },
  spaceSwitcher: {
    open: "Open",
    chip: (name) => `Space ${name} – switch space`,
    switch: "Switch space",
    other: "Settings of another space",
    search: "Search for a space",
    help: "Use the arrow keys to select, Enter to open.",
  },
};

export default en;
