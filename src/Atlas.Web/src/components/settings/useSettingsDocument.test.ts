import { describe, expect, it, vi } from "vitest";
import { defineComponent, h, ref } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { ApiError } from "../../api";
import { useSettingsDocument, type SettingsDocument, type SettingsDocumentOptions } from "./useSettingsDocument";

// The page tests pin what each page shows. These pin the lifecycle itself, once, with a fake whose
// requests the test answers by hand, so that the order of events is under its control.
interface Doc {
  version: string;
  name: string;
}
interface Pending<T> {
  resolve: (value: T) => void;
  reject: (failure: unknown) => void;
  promise: Promise<T>;
}
function pending<T>(): Pending<T> {
  let resolve!: Pending<T>["resolve"];
  let reject!: Pending<T>["reject"];
  const promise = new Promise<T>((done, fail) => ((resolve = done), (reject = fail)));
  return { resolve, reject, promise };
}

function mountDocument(extra: Partial<SettingsDocumentOptions<Doc>> = {}) {
  const name = ref("");
  const loads: (Pending<Doc> & { signal: AbortSignal })[] = [];
  const saves: (Pending<Doc> & { expectedVersion: string; name: string })[] = [];
  let doc!: SettingsDocument<Doc>;
  const wrapper = mount(defineComponent({
    setup() {
      doc = useSettingsDocument<Doc>({
        load: (signal) => {
          const request = Object.assign(pending<Doc>(), { signal });
          loads.push(request);
          return request.promise;
        },
        save: (current) => {
          const request = Object.assign(pending<Doc>(), { expectedVersion: current.version, name: name.value });
          saves.push(request);
          return request.promise;
        },
        fill: (current) => { name.value = current.name; },
        dirty: (current) => name.value !== current.name,
        ...extra,
      });
      return () => h("div");
    },
  }));
  return { name, loads, saves, wrapper, doc: () => doc };
}
/** A page that has loaded `v1` and whose draft was edited. */
async function loaded(extra: Partial<SettingsDocumentOptions<Doc>> = {}) {
  const page = mountDocument(extra);
  const doc = page.doc();
  void doc.load();
  page.loads[0]!.resolve({ version: "v1", name: "Atlas" });
  await flushPromises();
  page.name.value = "Wiki";
  return { ...page, doc };
}
const conflict = () => new ApiError(409, "Změněno jiným správcem");

describe("useSettingsDocument", () => {
  it("loads the document into the draft, and a failed load shows its message and its failure until the next load starts", async () => {
    const { doc, name, loads } = await loaded();
    expect(doc.saved.value).toEqual({ version: "v1", name: "Atlas" });
    expect(doc.loading.value).toBe(false);
    expect(doc.dirty.value).toBe(true);
    doc.reset();
    expect(name.value).toBe("Atlas");
    expect(doc.dirty.value).toBe(false);

    void doc.load();
    expect(doc.loading.value).toBe(true);
    loads[1]!.reject(new ApiError(500, "Server neodpovídá"));
    await flushPromises();
    expect(doc.loadError.value).toBe("Server neodpovídá");
    expect(doc.loadFailure.value).toBeInstanceOf(ApiError);
    expect(doc.loading.value).toBe(false);
    void doc.load();
    expect(doc.loadError.value).toBe("");
    expect(doc.loadFailure.value).toBeUndefined();
  });

  it("saves the draft with the version it loaded and takes the answer as the new document", async () => {
    const onSaved = vi.fn();
    const { doc, name, saves } = await loaded({ onSaved });
    void doc.save();
    void doc.save();
    expect(doc.saving.value).toBe(true);
    // A second click while the request runs sends nothing.
    expect(saves).toHaveLength(1);
    expect(saves[0]).toMatchObject({ expectedVersion: "v1", name: "Wiki" });
    saves[0]!.resolve({ version: "v2", name: "Wiki!" });
    await flushPromises();
    expect(doc.saved.value).toEqual({ version: "v2", name: "Wiki!" });
    expect(name.value).toBe("Wiki!");
    expect(doc.dirty.value).toBe(false);
    expect(doc.saving.value).toBe(false);
    expect(onSaved).toHaveBeenCalledWith({ version: "v2", name: "Wiki!" });
  });

  it("does not report a stored save as failed when onSaved throws", async () => {
    const { doc, saves } = await loaded({ onSaved: () => { throw new Error("Obsluha selhala"); } });
    const done = doc.save();
    saves[0]!.resolve({ version: "v2", name: "Wiki!" });
    await expect(done).rejects.toThrow("Obsluha selhala");
    expect(doc.saved.value).toEqual({ version: "v2", name: "Wiki!" });
    expect(doc.saveError.value).toBe("");
    expect(doc.conflict.value).toBe(false);
    expect(doc.saving.value).toBe(false);
  });

  it("keeps the draft and the message when a save fails", async () => {
    const onSaved = vi.fn();
    const { doc, name, saves } = await loaded({ onSaved });
    void doc.save();
    saves[0]!.reject(new ApiError(500, "Uložení se nezdařilo"));
    await flushPromises();
    expect(doc.saveError.value).toBe("Uložení se nezdařilo");
    expect(doc.conflict.value).toBe(false);
    expect(doc.saving.value).toBe(false);
    expect(name.value).toBe("Wiki");
    expect(doc.dirty.value).toBe(true);
    expect(onSaved).not.toHaveBeenCalled();
  });

  it("answers 409 with a conflict and the draft; by default the note stays while the next save runs and a failure of another kind replaces it", async () => {
    const { doc, name, saves } = await loaded();
    void doc.save();
    saves[0]!.reject(conflict());
    await flushPromises();
    expect(doc.conflict.value).toBe(true);
    expect(doc.saveError.value).toBe("Změněno jiným správcem");
    expect(name.value).toBe("Wiki");

    void doc.save();
    expect(doc.conflict.value).toBe(true);
    expect(doc.saveError.value).toBe("");
    saves[1]!.reject(new ApiError(500, "Uložení se nezdařilo"));
    await flushPromises();
    expect(doc.conflict.value).toBe(false);
  });

  it("closes the note when the next save starts if the page asks for it, and says nothing in the SaveBar when it keeps quiet", async () => {
    const { doc, saves } = await loaded({ clearConflictOnSave: true, quietConflict: true });
    void doc.save();
    saves[0]!.reject(conflict());
    await flushPromises();
    expect(doc.conflict.value).toBe(true);
    expect(doc.saveError.value).toBe("");

    void doc.save();
    expect(doc.conflict.value).toBe(false);
    saves[1]!.reject(new ApiError(500, "Uložení se nezdařilo"));
    await flushPromises();
    // Only a conflict is quiet.
    expect(doc.saveError.value).toBe("Uložení se nezdařilo");
  });

  it("refuses a save before anything is sent when the page says so, and leaves the conflict note alone", async () => {
    let refusal: string | undefined;
    const { doc, saves } = await loaded({ clearConflictOnSave: true, beforeSave: () => refusal });
    void doc.save();
    saves[0]!.reject(conflict());
    await flushPromises();

    refusal = "Opravte zvýrazněná pole.";
    await doc.save();
    expect(saves).toHaveLength(1);
    expect(doc.saveError.value).toBe("Opravte zvýrazněná pole.");
    expect(doc.saving.value).toBe(false);
    expect(doc.conflict.value).toBe(true);

    refusal = undefined;
    void doc.save();
    expect(saves).toHaveLength(2);
    expect(doc.saveError.value).toBe("");
  });

  it("drops the draft on reloadAfterConflict and takes the stored document", async () => {
    const { doc, name, saves, loads } = await loaded();
    void doc.save();
    saves[0]!.reject(conflict());
    await flushPromises();

    doc.reloadAfterConflict();
    expect(doc.conflict.value).toBe(false);
    expect(loads).toHaveLength(2);
    loads[1]!.resolve({ version: "v3", name: "Jiný správce" });
    await flushPromises();
    expect(name.value).toBe("Jiný správce");
    expect(doc.dirty.value).toBe(false);
  });

  it("leaves nothing of a load that a newer load has replaced (document, draft, error, failure) and aborts it", async () => {
    // A load can start while an earlier one still runs (a retry, Načíst znovu). Whatever the older one
    // answers, late and in either way, must not show in any state the composable exposes.
    const { doc, name, loads } = await loaded();
    void doc.load();
    void doc.load();
    expect(loads[1]!.signal.aborted).toBe(true);
    loads[2]!.resolve({ version: "v3", name: "Nový" });
    loads[1]!.resolve({ version: "v2", name: "Starý" });
    await flushPromises();
    expect(doc.saved.value).toEqual({ version: "v3", name: "Nový" });
    expect(name.value).toBe("Nový");
    expect(doc.loading.value).toBe(false);

    void doc.load();
    void doc.load();
    loads[3]!.reject(new ApiError(500, "Starý konec"));
    await flushPromises();
    expect(doc.loadError.value).toBe("");
    expect(doc.loadFailure.value).toBeUndefined();
    expect(doc.loading.value).toBe(true);
    loads[4]!.resolve({ version: "v4", name: "Nejnovější" });
    await flushPromises();
    expect(doc.saved.value).toEqual({ version: "v4", name: "Nejnovější" });
    expect(doc.loading.value).toBe(false);
  });

  it("starts no load once the page is gone", async () => {
    const page = mountDocument();
    page.wrapper.unmount();
    await page.doc().load();
    expect(page.loads).toHaveLength(0);
    expect(page.doc().saved.value).toBeUndefined();
  });

  it("aborts a load that is still running when the page goes away and does not adopt its answer", async () => {
    const page = mountDocument();
    void page.doc().load();
    page.wrapper.unmount();
    expect(page.doc().isDisposed()).toBe(true);
    expect(page.loads[0]!.signal.aborted).toBe(true);
    page.loads[0]!.resolve({ version: "v1", name: "Atlas" });
    await flushPromises();
    expect(page.doc().saved.value).toBeUndefined();
    expect(page.name.value).toBe("");
  });

  it.each([
    ["is stored", (save: Pending<Doc>) => save.resolve({ version: "v2", name: "Wiki" })],
    ["fails", (save: Pending<Doc>) => save.reject(conflict())],
  ])("does not adopt, report or announce a save that %s after the page is gone", async (_, answer) => {
    const onSaved = vi.fn();
    const { doc, saves, wrapper } = await loaded({ onSaved });
    void doc.save();
    wrapper.unmount();
    answer(saves[0]!);
    await flushPromises();
    expect(doc.saved.value!.version).toBe("v1");
    expect(doc.conflict.value).toBe(false);
    expect(doc.saveError.value).toBe("");
    expect(onSaved).not.toHaveBeenCalled();
  });
});
