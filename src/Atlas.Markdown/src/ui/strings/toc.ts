/** Keys of the `toc` section of `MarkdownStrings`; the Czech defaults live in plugins/toc/. */
export interface TocStrings {
  /** Module label (toolbar/slash item), and the fallback nav title/aria-label when no title is set. */
  defaultTitle: string
  /** Second line of the slash menu item. */
  slashDescription: string
  titleRequired: string
  /** Fallback heading text when a heading node has no readable text content. */
  headingWithoutText: string
  emptyDocument: string
  /** Accessible names of the in-place title and of the heading-range chip, and the "all levels" menu choice. */
  titleLabel: string
  rangeLabel: string
  allHeadings: string
}
