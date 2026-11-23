// @vitest-environment node
import { expect, it } from "vitest";
import { drawContactQr } from "./contactQr";

it("draws rounded rings on white with a quiet zone and plays the reveal once, not again on the next look", async () => {
  const text = "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Nováková;Jana;;;\r\nFN:Jana Nováková\r\nEND:VCARD\r\n";
  const first = await drawContactQr(text);
  // 8 rings of modules from the centre and the finder squares; the side is the matrix (17 + 4 × version) and 4 modules each way.
  expect(first.svg.match(/class="atlas-qr-band"/g)).toHaveLength(9);
  expect(first.svg).toContain(`viewBox="0 0 ${17 + 4 * first.version + 8} ${17 + 4 * first.version + 8}"`);
  expect(first.svg).toContain('fill="white"');
  expect(first.svg).not.toContain("data-shown");
  expect((await drawContactQr(text)).svg).toContain("data-shown");
});
