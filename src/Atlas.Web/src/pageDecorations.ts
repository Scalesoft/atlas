import { appearanceColors, isIconName, legacyAppearanceColors } from './appearance';

export interface PageDecoration { revisionId: string; icon: string | null; color: string | null }
/** A stored page icon Atlas draws: any well-formed name (the 87, the icon pack, a retired one a page already uses); AtlasIcon shows circle-help for one it does not know. */
export const isPageIcon = (icon: string | null | undefined): icon is string => !!icon && isIconName(icon);
export const isPageIconColor = (color: string | null | undefined): color is string =>
  !!color && (appearanceColors.some(item => item.id === color) || color in legacyAppearanceColors);
