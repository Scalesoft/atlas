import type { ContactQrCode } from "@atlas/markdown/plugins/contacts";
import "./contactQr.css";

/** A code shown again within this time appears at once: the reveal plays on the first look, not on every reopening of a card. */
const REPLAY_AFTER_MS = 10 * 60_000;
const QUIET_ZONE = 4;
/** Rings of modules from the centre outwards, one path each; the three finder squares come last, as one more ring. */
const BANDS = 8;
const shown = new Map<string, number>();

/** A module as a rounded square, slightly inset: dots to the eye, a full dark centre to a scanner. */
const dot = (x: number, y: number) => `M${x + 0.35},${y + 0.05}h0.3a0.3,0.3 0 0 1 0.3,0.3v0.3a0.3,0.3 0 0 1-0.3,0.3h-0.3a0.3,0.3 0 0 1-0.3-0.3v-0.3a0.3,0.3 0 0 1 0.3-0.3z`;
const rounded = (x: number, y: number, size: number, r: number) =>
  `M${x + r},${y}h${size - 2 * r}a${r},${r} 0 0 1 ${r},${r}v${size - 2 * r}a${r},${r} 0 0 1-${r},${r}h-${size - 2 * r}a${r},${r} 0 0 1-${r}-${r}v-${size - 2 * r}a${r},${r} 0 0 1 ${r}-${r}z`;
/** A finder square: a rounded 7 × 7 ring around a rounded 3 × 3 eye (evenodd cuts the ring). */
const finder = (x: number, y: number) => rounded(x, y, 7, 2.1) + rounded(x + 1, y + 1, 5, 1.5) + rounded(x + 2, y + 2, 3, 0.9);

/**
 * Draws the QR code of a contact card (the contacts plugin's `qrCode`; contactQr picks what goes in): uqr's matrix at error correction M
 * as rounded modules and finder squares, black on white with a four-module quiet zone. The modules come in rings from the centre
 * (contactQr.css: opacity and scale per ring from the motion tokens, a fade under reduced motion, nothing under `[data-still]`).
 */
export async function drawContactQr(text: string): Promise<ContactQrCode> {
  const { encode } = await import("uqr");
  const { data, size, version } = encode(text, { ecc: "M", border: 0 });
  const inFinder = (x: number, y: number) => (x < 7 || x >= size - 7) && (y < 7 || y >= size - 7) && !(x >= size - 7 && y >= size - 7);
  const centre = (size - 1) / 2, bands: string[] = Array.from({ length: BANDS }, () => "");
  data.forEach((row, y) => row.forEach((dark, x) => {
    if (!dark || inFinder(x, y)) return;
    const ring = Math.max(Math.abs(x - centre), Math.abs(y - centre)) / (centre + 1);
    bands[Math.min(BANDS - 1, Math.floor(ring * BANDS))] += dot(x + QUIET_ZONE, y + QUIET_ZONE);
  }));
  const far = size - 7 + QUIET_ZONE;
  const paths = [...bands, finder(QUIET_ZONE, QUIET_ZONE) + finder(far, QUIET_ZONE) + finder(QUIET_ZONE, far)]
    .map((d, band) => `<path class="atlas-qr-band" style="--qr-band:${band}" fill-rule="evenodd" d="${d}"/>`).join("");
  const side = size + 2 * QUIET_ZONE, now = Date.now(), again = now - (shown.get(text) ?? -Infinity) < REPLAY_AFTER_MS;
  shown.set(text, now);
  return { version, svg: `<svg xmlns="http://www.w3.org/2000/svg" class="atlas-qr"${again ? " data-shown" : ""} viewBox="0 0 ${side} ${side}"><rect width="${side}" height="${side}" fill="white"/><g fill="black">${paths}</g></svg>` };
}
