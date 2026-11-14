import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `outline` section of `MarkdownStrings` (see ../strings.ts). */
export interface OutlineStrings {
  toggleHide: string
  toggleShow: string
  railLabel: string
  /** "{title}, úroveň {level}" aria-label for a rail entry button. */
  railEntryLabel: (title: string, level: number) => string
  panelLabel: string
  unpinLabel: string
  pinLabel: string
  collapseLabel: string
  emptyHint: string
  livePreviewCaption: string
  /** Fallback title for a heading with no text content. */
  untitledHeading: string
  /** Shown in place of a canvas the live preview cannot mirror. */
  previewUnavailable: string
}

export const outlineStrings: OutlineStrings = registerMarkdownStrings('outline', {
  toggleHide: 'Skrýt obsah',
  toggleShow: 'Zobrazit obsah',
  railLabel: 'Miniatury nadpisů',
  railEntryLabel: (title, level) => `${title}, úroveň ${level}`,
  panelLabel: 'Obsah dokumentu',
  unpinLabel: 'Odepnout obsah',
  pinLabel: 'Připnout obsah nahoru',
  collapseLabel: 'Sbalit obsah do ikonky',
  emptyHint: 'Přidejte do dokumentu nadpis.',
  livePreviewCaption: 'Živý výřez',
  untitledHeading: 'Bez názvu',
  previewUnavailable: 'Náhled grafiky není dostupný',
} satisfies OutlineStrings)
