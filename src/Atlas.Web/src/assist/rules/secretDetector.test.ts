// @vitest-environment node
import { describe, expect, it } from "vitest";
import { createSecretExamples } from "./examples";
import { detectSecrets } from "./secretDetector";
import { spanText } from "./span";

const found = (text: string) => detectSecrets(text).map(hit => [hit.kind, spanText(text, hit.span)]);

describe("secret detector (E7)", () => {
  it("finds prefixed values, known key formats and high-entropy tokens", () => {
    expect(found("Nouzový účet: admin, heslo Srv#2026!rack")).toEqual([["prefixed", "Srv#2026!rack"]]);
    expect(found("V .env je DB_PASSWORD=\"Winter2026!x\" a api klíč: q7Tr2pLm9x.")).toEqual([
      ["prefixed", "Winter2026!x"], ["prefixed", "q7Tr2pLm9x"],
    ]);
    expect(found("Token ghp_aB3dE5fG7hJ9kL1mN3pQ5rS7tU9vW1xY3zA a klíč AKIAIOSFODNN7EXAMPLE.")).toEqual([
      ["key-format", "ghp_aB3dE5fG7hJ9kL1mN3pQ5rS7tU9vW1xY3zA"], ["key-format", "AKIAIOSFODNN7EXAMPLE"],
    ]);
    const pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA7bq9\n-----END RSA PRIVATE KEY-----";
    expect(found(`Klíč serveru:\n${pem}\nKonec.`)).toEqual([["private-key", pem]]);
    expect(found("Pro export použijte xK3m9QpL2vR7tY8wZ4 nebo Zq8/Lm2xR7tB4nW9+kD3vH6yP1sJ5fG0, kód dGhpcyBpcyBhIHNlY3JldCAxMjM0NQ==.")).toEqual([
      ["high-entropy", "xK3m9QpL2vR7tY8wZ4"], ["high-entropy", "Zq8/Lm2xR7tB4nW9+kD3vH6yP1sJ5fG0"],
      ["high-entropy", "dGhpcyBpcyBhIHNlY3JldCAxMjM0NQ=="],
    ]);
  });

  it("does not flag UUIDs, URLs, e-mail addresses or short words", () => {
    const prose = [
      "Stránka 0a1b2c3d-4e5f-6789-abcd-ef0123456789 je na https://atlas.example.cz/#/pages/0a1b2c3d?heading=Pristup&token=x.",
      "Pište na jana.vesela@example.test nebo volejte +420 731 555 010.",
      "Heslo musí mít alespoň 14 znaků. Password must be strong. Token je platný 30 dní.",
      "Nejpravděpodobnější příčinou je Přihlašovací formulář; Zaokrouhlovat se nemusí.",
      "Soubor src/Atlas.Web/src/assist/rules/intentRules.ts patří do Atlas.Web, cesta C:\\Projekty\\Atlas\\x64Release2026.",
      "SHA-256 souboru je 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08, commit 4306484be1f0c2a9d7e3b5f6a8c9d0e1f2a3b4c5.",
      "Obraz ghcr.io/example/atlas:1.5.0@sha256:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 je podepsaný.",
      "Server fe80::1ff:fe23:4567:890a odpovídá, $ pwd vypíše /home/atlas/releases/2026-10-04T21:22:00Z.",
      "V konfiguraci zůstane password=${DB_PASSWORD} a heslo: ******** jako zástupný znak.",
    ];
    expect(prose.flatMap(text => found(text))).toEqual([]);
  });

  it("returns ranges without retaining the value", () => {
    const text = "Do konfigurace zadejte password=Winter2026!x";
    const hits = detectSecrets(text);
    expect(hits).toEqual([{ kind: "prefixed", span: { start: 32, end: 44 } }]);
    expect(JSON.stringify(hits)).not.toContain("Winter");

    // "Je to jen příklad" is remembered per page as a hash of the span, never as text.
    const examples = createSecretExamples();
    examples.mark("page-1", "Winter2026!x");
    expect(examples.isExample("page-1", "Winter2026!x")).toBe(true);
    expect(examples.isExample("page-2", "Winter2026!x")).toBe(false);
    expect(examples.isExample("page-1", "Winter2026!y")).toBe(false);
    expect(JSON.stringify(examples)).not.toContain("Winter");
  });

  it("stays linear on a long run of keyword segments (no backtracking freeze)", () => {
    const started = performance.now();
    expect(found(`${"token_".repeat(5000)}x a ${"secret.".repeat(5000)}x`)).toEqual([]);
    expect(performance.now() - started).toBeLessThan(200);
    expect(found("Prefix my_api key: q7Tr2pLm9x, x.client-secret = Winter2026!x a passwords: Winter2026!x.")).toEqual([
      ["prefixed", "q7Tr2pLm9x"], ["prefixed", "Winter2026!x"],
    ]);
    // A Czech name before the keyword (diacritics, then an ASCII tail) still names the secret.
    expect(found("účet_heslo: Winter2026!x a uživatel-token: q7Tr2pLm9x")).toEqual([
      ["prefixed", "Winter2026!x"], ["prefixed", "q7Tr2pLm9x"],
    ]);
  });
});
