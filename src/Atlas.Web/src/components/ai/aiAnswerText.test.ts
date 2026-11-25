// @vitest-environment node
import { expect, it } from "vitest";
import { sourceCount, sourceLabel, sourceRoute, tokenizeSentence } from "./aiAnswerText";

it("turns backticks into code and leaves tags, links and urls as text", () => {
  expect(tokenizeSentence("Spusťte `vpn connect` a počkejte.")).toEqual([
    { kind: "text", text: "Spusťte " }, { kind: "code", text: "vpn connect" }, { kind: "text", text: " a počkejte." },
  ]);
  const hostile = "<img src=x onerror=alert(1)> [odkaz](https://example.test) https://example.test/a `<b>` ";
  expect(tokenizeSentence(hostile)).toEqual([
    { kind: "text", text: "<img src=x onerror=alert(1)> [odkaz](https://example.test) https://example.test/a " },
    { kind: "code", text: "<b>" }, { kind: "text", text: " " },
  ]);
  expect(tokenizeSentence("Jedna ` zbloudilá závorka")).toEqual([{ kind: "text", text: "Jedna ` zbloudilá závorka" }]);
  expect(tokenizeSentence("")).toEqual([]);
});

it("turns a **bold** key fact into a strong token and leaves stray asterisks as text", () => {
  expect(tokenizeSentence("Hotovo **34 %**, `vpn` čeká.")).toEqual([
    { kind: "text", text: "Hotovo " }, { kind: "strong", text: "34 %" }, { kind: "text", text: ", " }, { kind: "code", text: "vpn" }, { kind: "text", text: " čeká." },
  ]);
  expect(tokenizeSentence("Jen *jedna* a ** nic")).toEqual([{ kind: "text", text: "Jen *jedna* a ** nic" }]);
});

it("builds the current page route at its heading and labels sources", () => {
  expect(sourceRoute({ pageId: "p 1", headingId: "h/2" })).toBe("#/pages/p%201?heading=h%2F2");
  expect(sourceRoute({ pageId: "p1", headingId: null })).toBe("#/pages/p1");
  expect(sourceLabel({ title: "VPN", headingTitle: "Z domova" })).toBe("VPN › Z domova");
  expect(sourceLabel({ title: "VPN", headingTitle: null })).toBe("VPN");
  expect([1, 2, 4, 5, 0].map(sourceCount)).toEqual(["1 zdroj", "2 zdroje", "4 zdroje", "5 zdrojů", "0 zdrojů"]);
});
