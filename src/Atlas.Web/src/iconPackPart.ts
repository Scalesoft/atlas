/**
 * The icon pack's drawings ship in parts (the `iconPackParts` plugin of vite.config.ts splits assets/icon-pack/markup.json):
 * a view downloads only the parts of the pack icons it draws, about 1.7 KiB brotli each instead of 71 KiB for the whole
 * pack. The build and the app pick the part of a name with this one hash.
 */
export const iconPackPartCount = 64;

export function iconPackPart(name: string): number {
  let hash = 0;
  for (let index = 0; index < name.length; index++) hash = (Math.imul(hash, 31) + name.charCodeAt(index)) >>> 0;
  return hash % iconPackPartCount;
}
