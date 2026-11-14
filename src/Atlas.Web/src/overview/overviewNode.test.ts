// @vitest-environment node
import { expect, it } from "vitest";
import { createRequirementsPlugin } from "../projector/requirementsPlugin";
import "../contacts/contactOverview";

it("offers „Přehled: Kontakty“ without the projector module, „Přehled: Stav nasazení“ only with it (R6)", () => {
  const overviews = (offered: boolean) => (createRequirementsPlugin({ offered: () => offered }).create({} as never).actions?.slash ?? [])
    .filter(action => action.id?.startsWith("overview.")).map(action => action.label);
  expect(overviews(false)).toEqual(["Přehled: Kontakty"]);
  expect(overviews(true)).toEqual(["Přehled: Stav nasazení", "Přehled: Kontakty"]);
});
