/**
 * Focus movement in the results of AppearanceIconBrowser: groups of options, each a grid that starts on a new row.
 * Kept apart from the component so the rules can be tested without layout.
 */
export type IconGridKey = "ArrowLeft" | "ArrowRight" | "ArrowUp" | "ArrowDown" | "Home" | "End";
export interface IconGridPosition { section: number; index: number }

/** `sections`: options per group (never zero); every group starts a new row and has `columns` columns. Nothing wraps. */
export function moveIconFocus(sections: readonly number[], at: IconGridPosition, key: IconGridKey, columns: number): IconGridPosition {
  const last = (section: number) => sections[section]! - 1;
  const { section, index } = at;
  const column = index % columns;
  switch (key) {
    case "Home": return { section: 0, index: 0 };
    case "End": return { section: sections.length - 1, index: last(sections.length - 1) };
    case "ArrowRight":
      if (index < last(section)) return { section, index: index + 1 };
      return section < sections.length - 1 ? { section: section + 1, index: 0 } : at;
    case "ArrowLeft":
      if (index > 0) return { section, index: index - 1 };
      return section > 0 ? { section: section - 1, index: last(section - 1) } : at;
    case "ArrowDown": {
      if (index + columns <= last(section)) return { section, index: index + columns };
      // A shorter last row below gives its last option.
      if (Math.floor(index / columns) < Math.floor(last(section) / columns)) return { section, index: last(section) };
      return section < sections.length - 1 ? { section: section + 1, index: Math.min(column, last(section + 1)) } : at;
    }
    case "ArrowUp": {
      if (index >= columns) return { section, index: index - columns };
      if (section === 0) return at;
      // The last row of the group above in the same column; when that row is shorter, the row before it or the group's last option.
      const end = last(section - 1);
      const below = Math.floor(end / columns) * columns + column;
      return { section: section - 1, index: below <= end ? below : below >= columns ? below - columns : end };
    }
  }
}
