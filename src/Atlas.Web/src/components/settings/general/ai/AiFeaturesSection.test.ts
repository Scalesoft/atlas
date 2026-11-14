import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../../api";
import { mountPage } from "../testing";
import AiFeaturesSection from "./AiFeaturesSection.vue";
import { useAiMock } from "../../../../testing/aiTesting";

let wrapper: VueWrapper | undefined;
async function open(options: Parameters<typeof useAiMock>[0] = { enabled: true, configured: true }) {
  const harness = useAiMock(options);
  const mounted = mountPage(AiFeaturesSection, {}, { hash: "#/settings/general/modules/ai/features" });
  wrapper = mounted.wrapper;
  await flushPromises();
  return { ...harness, view: mounted.wrapper };
}
const row = (view: VueWrapper, task: string) => view.get(`[data-ai-feature="${task}"]`);
const limit = (view: VueWrapper, task: string) => view.get(`[data-ai-limit="${task}"] [data-ai-limit-input]`);
const save = (view: VueWrapper) => view.get("[data-save-bar-save]").trigger("click");
type Sent = { items: Record<string, unknown>[] };

beforeEach(() => clearCsrf());
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

describe("Administrace → AI → Funkce", () => {
  it("edits a daily limit only for limitable tasks and treats empty as unlimited", async () => {
    const { calls, view } = await open();
    // The answer tasks sit in their groups; only they offer a limit.
    expect(view.get('[data-ai-group="search"]').text()).toContain("Odpověď z wiki");
    expect(view.get('[data-ai-group="read"]').text()).toContain("Asistent");
    expect(view.get('[data-ai-group="read"]').text()).toContain("Asistent s nástroji");
    expect(view.get('[data-ai-group="write"]').text()).toContain("Asistent čte rozepsané verze");
    expect(view.find('[data-ai-limit="page.related"]').exists()).toBe(false);
    expect(view.get('[data-ai-limit-section] button[aria-expanded]').attributes('aria-expanded')).toBe('false');
    await view.get('[data-ai-limit-section] button[aria-expanded]').trigger('click');
    expect(view.findAll('[data-ai-limit-input]')).toHaveLength(3);
    expect((limit(view, "search.answer").element as HTMLInputElement).value).toBe("");
    expect(view.get('[data-ai-limit="search.answer"]').text()).toContain("Prázdné pole znamená bez limitu");

    // A value outside 1..10 000 or a non-integer stops the save and says why next to the field.
    await limit(view, "search.answer").setValue("0");
    await view.get('[data-ai-limit-section] button[aria-expanded]').trigger('click');
    await save(view);
    await flushPromises();
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
    expect(view.get('[data-ai-limit-section] button[aria-expanded]').attributes('aria-expanded')).toBe('true');
    expect(document.activeElement).toBe(limit(view, "search.answer").element);
    expect(view.get('[data-ai-limit="search.answer"]').text()).toContain("celé číslo od 1 do 10 000");
    await limit(view, "search.answer").setValue("12.5");
    await save(view);
    await flushPromises();
    expect(calls.some((call) => call.method === "PUT")).toBe(false);

    await limit(view, "search.answer").setValue("25");
    await save(view);
    await flushPromises();
    let put = calls.filter((call) => call.method === "PUT").at(-1)!.body as Sent;
    expect(put.items.find((item) => item.task === "search.answer")).toMatchObject({ dailyLimit: 25 });
    expect(put.items.find((item) => item.task === "chat.answer")).toMatchObject({ dailyLimit: null });
    // A task that is not limitable never carries the field.
    expect("dailyLimit" in put.items.find((item) => item.task === "page.related")!).toBe(false);
    expect((limit(view, "search.answer").element as HTMLInputElement).value).toBe("25");

    // Emptying the field removes the limit: null, not 0 and not left out.
    await limit(view, "search.answer").setValue("");
    await save(view);
    await flushPromises();
    put = calls.filter((call) => call.method === "PUT").at(-1)!.body as Sent;
    expect(put.items.find((item) => item.task === "search.answer")).toMatchObject({ dailyLimit: null });
  });

  it("keeps a task list from a server without limits working", async () => {
    const harness = useAiMock({ enabled: true, configured: true });
    for (const item of harness.mock.state.features.items as Record<string, unknown>[]) { delete item.limitable; delete item.dailyLimit; }
    const mounted = mountPage(AiFeaturesSection, {}, { hash: "#/settings/general/modules/ai/features" });
    wrapper = mounted.wrapper;
    await flushPromises();
    expect(wrapper.find("[data-ai-limit-section]").exists()).toBe(false);
    expect(wrapper.get('[data-ai-group="write"]').text()).toContain("Rozepsaný text pro tyto funkce zůstává ve firmě.");
    expect(wrapper.find("[data-ai-cloud-permissions]").exists()).toBe(false);
    await row(wrapper, "search.answer").get("[data-ai-feature-switch]").trigger("click");
    await save(wrapper);
    await flushPromises();
    const put = harness.calls.find((call) => call.method === "PUT")!.body as Sent;
    expect("dailyLimit" in put.items[0]!).toBe(false);
  });
});

it("shows working-text consent only for enabled cloud tasks, including Quick routed through Chat, and preserves hidden choices", async () => {
  const harness=useAiMock({enabled:true,configured:true}, (_method,path,body)=> {
    if(path==="/admin/ai/features" && _method==="PUT") return {version:"f2",items:(body as Sent).items};
    if(path==="/admin/ai/features") return {version:null,items:[{task:"editor.transform",group:"write",enabled:true,profileKind:"quick",cloudWorkingText:false},{task:"editor.link",group:"write",enabled:true,profileKind:"embeddings",cloudWorkingText:true}]};
    if(path==="/admin/ai/profiles/embeddings") return {configured:true,location:"company"};
    if(path==="/admin/ai/profiles/quick") return {configured:true,location:"company",useChatProfile:true};
    if(path==="/admin/ai/profiles/chat") return {configured:true,location:"cloud"};
  });
  wrapper=mountPage(AiFeaturesSection).wrapper;await flushPromises();
  expect(row(wrapper,"editor.transform").find('[role="checkbox"]').exists()).toBe(false);
  expect(row(wrapper,"editor.link").find('[role="checkbox"]').exists()).toBe(false);
  expect(wrapper.get('[data-ai-cloud-consent="editor.transform"] [role="checkbox"]').attributes("data-disabled")).toBeUndefined();
  expect(wrapper.find('[data-ai-cloud-consent="editor.link"]').exists()).toBe(false);
  expect(wrapper.get('[data-ai-cloud-permissions="write"]').text()).toContain("nepublikovaný text");
  expect(wrapper.findAll('[data-ai-cloud-permissions="write"] p')).toHaveLength(1);
  expect(wrapper.get('[data-ai-group="write"]').text()).not.toContain("Profil:");

  await wrapper.get('[data-ai-cloud-consent="editor.transform"] [role="checkbox"]').trigger("click");
  await row(wrapper,"editor.transform").get('[data-ai-feature-switch]').trigger("click");
  expect(wrapper.find('[data-ai-cloud-permissions="write"]').exists()).toBe(false);
  await save(wrapper);
  await flushPromises();
  const sent = harness.calls.find(c=>c.method==="PUT")!.body as Sent;
  expect(sent.items.find(item=>item.task==="editor.transform")).toMatchObject({enabled:false,cloudWorkingText:true});
  expect(sent.items.find(item=>item.task==="editor.link")).toMatchObject({enabled:true,cloudWorkingText:true});
  expect(harness.calls.some(c=>c.path==="/admin/ai/profiles/chat")).toBe(true);
});
