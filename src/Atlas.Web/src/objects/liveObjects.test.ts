// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { ApiError } from "../api";
import { applyObjectChange, createLiveObjects, liveObjectOf, type LiveObject } from "./liveObjects";
import type { ObjectDto } from "./objectApi";

const jana: LiveObject = { id: "c1", typeId: "atlas.contact", version: 3, title: "Jana", data: { name: "Jana", title: "Ředitelka" }, canEdit: true, available: true };

describe("applyObjectChange", () => {
  it("merges a field change of the next version, a null clearing the field", () => {
    expect(applyObjectChange(jana, { objectId: "c1", version: 4, change: { fields: { title: null, phones: ["777"] } } }))
      .toEqual({ ...jana, version: 4, data: { name: "Jana", phones: ["777"] } });
  });

  it("ignores what it already has and asks to read again after a skipped version or a change it cannot merge", () => {
    expect(applyObjectChange(jana, { objectId: "c1", version: 3, change: { fields: { title: "x" } } })).toBe("stale");
    expect(applyObjectChange(jana, { objectId: "c1", version: 5, change: { fields: { title: "x" } } })).toBe("refresh");
    expect(applyObjectChange(jana, { objectId: "c1", version: 4, change: { operations: [] } })).toBe("refresh");
    expect(applyObjectChange({ ...jana, data: null }, { objectId: "c1", version: 4, change: { fields: {} } })).toBe("refresh");
  });
});

describe("liveObjectOf", () => {
  it("reads a board card's target and an object of the object path, and a locked target without its content", () => {
    expect(liveObjectOf({ type: "atlas.contact", resourceId: "c1", title: "Jana", available: true, version: 3, canEdit: true, data: { name: "Jana", title: "Ředitelka" } })).toEqual(jana);
    expect(liveObjectOf({ id: "c1", typeId: "atlas.contact", version: 3, title: "Jana", canEdit: false, data: { name: "Jana" } }))
      .toEqual({ ...jana, canEdit: false, data: { name: "Jana" } });
    expect(liveObjectOf({ type: "atlas.contact", resourceId: "c2", title: "x", available: false, data: { name: "x" } }))
      .toEqual({ id: "c2", typeId: "atlas.contact", version: 0, title: "", data: null, canEdit: false, available: false });
    expect(liveObjectOf({ title: "no id" })).toBeNull();
  });
});

describe("createLiveObjects", () => {
  const dto = { id: "c1", typeId: "atlas.contact", version: 3, title: "Jana", canEdit: true, data: { name: "Jana", title: "Ředitelka" } } as unknown as ObjectDto;

  it("loads an object once, applies what its viewers hear and locks it when revoked", async () => {
    const get = vi.fn(async () => dto);
    const objects = createLiveObjects({ get, change: vi.fn() });
    await Promise.all([objects.load("c1"), objects.load("c1")]);
    expect(get).toHaveBeenCalledTimes(1);
    objects.apply({ objectId: "c1", version: 4, change: { fields: { title: "Jednatelka" } } });
    expect(objects.get("c1")?.data).toEqual({ name: "Jana", title: "Jednatelka" });
    // A skipped version reads the object again.
    objects.apply({ objectId: "c1", version: 9, change: { fields: { title: "?" } } });
    await vi.waitFor(() => expect(get).toHaveBeenCalledTimes(2));
    objects.revoke("c1");
    expect(objects.get("c1")).toMatchObject({ available: false, data: null });
  });

  it("an object that is gone or never readable reads as locked", async () => {
    const objects = createLiveObjects({ get: async () => { throw new ApiError(404, "Objekt nebyl nalezen."); }, change: vi.fn() });
    await objects.load("c9");
    expect(objects.get("c9")).toMatchObject({ id: "c9", available: false, data: null });
  });

  it("shows an edit at once, takes the version the server answers and keeps a colleague's change heard meanwhile", async () => {
    let answer: (value: { version: number; duplicate: boolean }) => void = () => {};
    const change = vi.fn(() => new Promise<{ version: number; duplicate: boolean }>(resolve => { answer = resolve; }));
    const objects = createLiveObjects({ get: vi.fn(async () => dto), change });
    objects.seed(jana);
    const saving = objects.edit("c1", { title: "Jednatelka", organization: null });
    expect(objects.get("c1")?.data).toEqual({ name: "Jana", title: "Jednatelka" });
    expect(change).toHaveBeenCalledWith("c1", { fields: { title: "Jednatelka", organization: null } });
    objects.apply({ objectId: "c1", version: 4, change: { fields: { title: "Kolegyně", phones: ["777"] } } });
    answer({ version: 5, duplicate: false });
    await saving;
    expect(objects.get("c1")).toMatchObject({ version: 5, data: { name: "Jana", title: "Jednatelka", phones: ["777"] } });
  });

  it("reads the object again when an edit is refused, and says why", async () => {
    const get = vi.fn(async () => dto);
    const objects = createLiveObjects({ get, change: async () => { throw new ApiError(400, "Neplatné pole kontaktu: E-mail.", "invalid_object_change"); } });
    objects.seed(jana);
    await expect(objects.edit("c1", { emails: ["x"] })).rejects.toThrow("E-mail");
    expect(get).toHaveBeenCalled();
    expect(objects.get("c1")?.data).toEqual(dto.data);
  });

  it("puts back what was here when an edit is refused and the object cannot be read again (offline)", async () => {
    const objects = createLiveObjects({ get: async () => { throw new TypeError("Failed to fetch"); }, change: async () => { throw new ApiError(400, "Neplatné pole kontaktu: E-mail."); } });
    objects.seed(jana);
    await expect(objects.edit("c1", { emails: ["x"] })).rejects.toThrow("E-mail");
    expect(objects.get("c1")).toEqual(jana);
  });
});
