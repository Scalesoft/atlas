import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import SecretPicker from "./SecretPicker.vue";
import type { SecretReferenceView, SecretsApi } from "./secretsApi";

const secret: SecretReferenceView = {
  id: "0b6a0d9e-6e4e-4a8e-9a3f-1c2d3e4f5a61", title: "Serverovna — nouzový účet", path: "Provoz/Serverovna", vaultId: "v1", vaultName: "Provoz",
  spaceId: "space", fields: ["userName", "password"], level: "read",
};

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

function open(props: Record<string, unknown>) {
  const search = vi.fn(async () => [secret]);
  const api = { search } as unknown as SecretsApi;
  wrapper = mount(SecretPicker, { props: { open: true, spaceId: "space", api, ...props }, attachTo: document.body });
  return { search };
}
const pickFirst = async () => {
  await flushPromises();
  const option = [...document.body.querySelectorAll<HTMLElement>("[role=option], button")].find(element => element.textContent?.includes("Serverovna — nouzový účet"))!;
  option.click();
  await flushPromises();
};

describe("SecretPicker", () => {
  it("inserts a card or an inline reference in the ordinary mode", async () => {
    const { search } = open({});
    expect(document.body.textContent).toContain("Zobrazení");
    expect(search).toHaveBeenCalledWith("", "space", expect.any(AbortSignal));
    await pickFirst();
    expect(wrapper!.emitted("insert")).toEqual([[{ secretId: secret.id, inline: false, vaultName: "Provoz" }]]);
  });

  it("replaces the text with an inline reference in „Přesunout do trezoru“, searching the words around it once", async () => {
    vi.useFakeTimers();
    try {
      const { search } = open({ mode: "replace", initialQuery: "Nouzový účet serverovny" });
      expect(document.body.textContent).toContain("Uložte heslo do trezoru (KeePass) a vyberte ho. Text v dokumentu se nahradí odkazem.");
      expect(document.body.textContent).not.toContain("Zobrazení");
      expect((document.body.querySelector("input") as HTMLInputElement).value).toBe("Nouzový účet serverovny");
      await vi.advanceTimersByTimeAsync(500);
      expect(search.mock.calls).toEqual([["Nouzový účet serverovny", "space", expect.any(AbortSignal)]]);
    } finally { vi.useRealTimers(); }
    await pickFirst();
    expect(wrapper!.emitted("insert")).toEqual([[{ secretId: secret.id, inline: true, vaultName: "Provoz" }]]);
  });

  it("swaps a reference in „Vyměnit heslo“: opens on its group, marks the current secret and answers only an id", async () => {
    const { search } = open({ mode: "swap", initialQuery: "Provoz/Serverovna", currentId: secret.id });
    expect(document.body.textContent).not.toContain("Zobrazení");
    expect(search).toHaveBeenCalledWith("Provoz/Serverovna", "space", expect.any(AbortSignal));
    await flushPromises();
    expect(document.body.querySelector('[role="radio"][data-state="checked"]')?.textContent).toContain("Serverovna — nouzový účet");
    await pickFirst();
    expect(wrapper!.emitted("insert")![0]![0]).toMatchObject({ secretId: secret.id });
  });
});
