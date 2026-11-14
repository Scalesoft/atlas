import {afterEach,expect,it,vi} from "vitest";
import {mount,flushPromises,type VueWrapper} from "@vue/test-utils";
import ReadonlySelectionActions from "./ReadonlySelectionActions.vue";
const toast=vi.hoisted(()=>({success:vi.fn(),error:vi.fn()}));
vi.mock("../../composables/useToast",()=>({useToast:()=>({...toast,warning:vi.fn(),dismissAll:vi.fn()})}));
let view:VueWrapper;afterEach(()=>{view?.unmount();document.body.innerHTML="";vi.unstubAllGlobals();vi.clearAllMocks();history.replaceState(null,"","/");});
it("keeps focus and passes only the projected selection on explicit action",async()=>{const origin=document.createElement('button');document.body.append(origin);origin.focus();const selection={text:"Bezpečný text ⟦c:0123456789abcdef:1⟧",anchorRect:new DOMRect(200,200,160,20),sectionHref:"#/pages/p?heading=h"};view=mount(ReadonlySelectionActions,{props:{selection,enabled:true},attachTo:document.body});await flushPromises();expect(document.activeElement).toBe(origin);(document.querySelector('[data-readonly-explain]') as HTMLElement).click();expect(view.emitted('explain')?.[0]).toEqual([selection.text]);});
it("leaves no action surface without capability or a selection",async()=>{view=mount(ReadonlySelectionActions,{props:{selection:null,enabled:false},attachTo:document.body});await flushPromises();expect(document.querySelector('[data-readonly-actions]')).toBeNull();});

it.each([false,true])("copies the section link and searches locally with AI explanation available=%s",async(explainEnabled)=>{
  history.replaceState(null,"","/?q=private#/pages/old");
  const writeText=vi.fn().mockResolvedValue(undefined);vi.stubGlobal("navigator",{clipboard:{writeText}});
  view=mount(ReadonlySelectionActions,{props:{enabled:true,explainEnabled,selection:{text:"Bezpečný text",anchorRect:new DOMRect(200,200,160,20),sectionHref:"#/pages/p?heading=h"}},attachTo:document.body});await flushPromises();
  expect(!!document.querySelector('[data-readonly-explain]')).toBe(explainEnabled);
  (document.querySelector('[data-readonly-find]') as HTMLElement).click();
  expect(view.emitted('find')?.[0]).toEqual(['Bezpečný text']);
  const button=[...document.querySelectorAll<HTMLButtonElement>("[data-readonly-actions] button")].find(button=>button.textContent?.includes("Kopírovat odkaz na sekci"))!;
  button.click();await flushPromises();expect(writeText).toHaveBeenCalledWith(`${location.origin}/#/pages/p?heading=h`);
});

it("leads with Kopírovat, then the assistant actions, and hands them the selection",async()=>{
  view=mount(ReadonlySelectionActions,{props:{enabled:true,selection:{text:"Composition API",anchorRect:new DOMRect(200,200,160,20)}},attachTo:document.body});await flushPromises();
  const labels=[...document.querySelectorAll("[data-readonly-actions] button")].map(button=>button.textContent?.trim());
  expect(labels.slice(0,4)).toEqual(["Kopírovat","Zeptat se","Vysvětlit","Shrnout"]);
  (document.querySelector('[data-readonly-ask]') as HTMLElement).click();
  (document.querySelector('[data-readonly-summarize]') as HTMLElement).click();
  expect(view.emitted('ask')?.[0]).toEqual(["Composition API"]);
  expect(view.emitted('summarize')?.[0]).toEqual(["Composition API"]);
  await view.setProps({selection:{text:"x".repeat(1901),anchorRect:new DOMRect(200,200,160,20)}});
  expect(document.querySelector('[data-readonly-ask]')).toBeNull();
  expect(document.querySelector('[data-readonly-find]')).not.toBeNull();
});

it("names a widened section in visible text before the AI actions, not only in a title (P11)",async()=>{
  view=mount(ReadonlySelectionActions,{props:{enabled:true,selection:{text:"Úvod",section:"Úvod",anchorRect:new DOMRect(200,200,160,20)}},attachTo:document.body});await flushPromises();
  expect(document.querySelector("[data-readonly-target]")?.textContent).toBe("Sekce „Úvod“");
  await view.setProps({selection:{text:"Composition API",anchorRect:new DOMRect(200,200,160,20)}});
  expect(document.querySelector("[data-readonly-target]")).toBeNull();
});

it("keeps the non-AI actions when the selection holds only an element or a secret (P11)",async()=>{
  view=mount(ReadonlySelectionActions,{props:{enabled:true,selection:{text:null,searchText:null,anchorRect:new DOMRect(200,200,160,20),sectionHref:"#/pages/p?heading=h"}},attachTo:document.body});await flushPromises();
  expect(document.querySelector('[data-readonly-actions]')).not.toBeNull();
  expect(document.querySelector('[data-readonly-ask]')).toBeNull();
  expect(document.querySelector('[data-readonly-find]')).toBeNull();
  expect(document.querySelector('[data-readonly-actions]')!.textContent).toContain("Kopírovat odkaz na sekci");
  await view.setProps({selection:{text:null,searchText:null,anchorRect:new DOMRect(200,200,160,20)}});
  expect(document.querySelector('[data-readonly-actions]')).toBeNull();
});

const selected=()=>{const text=document.createElement("p");text.textContent="Bezpečný text";document.body.append(text);const range=document.createRange();range.selectNodeContents(text);document.getSelection()!.removeAllRanges();document.getSelection()!.addRange(range);};
const key=(target:EventTarget,name:string,init:KeyboardEventInit={})=>target.dispatchEvent(new KeyboardEvent("keydown",{key:name,bubbles:true,cancelable:true,...init}));
const focused=()=>document.activeElement?.textContent?.trim();

it("Kopírovat copies as the browser does, the selected text when it refuses, and says „Zkopírováno“",async()=>{
  selected();
  view=mount(ReadonlySelectionActions,{props:{enabled:true,selection:{text:"Bezpečný text",anchorRect:new DOMRect(200,200,160,20)}},attachTo:document.body});await flushPromises();
  const exec=vi.fn(()=>true);document.execCommand=exec;
  (document.querySelector('[data-readonly-copy]') as HTMLElement).click();await flushPromises();
  expect(exec).toHaveBeenCalledWith("copy");
  expect(toast.success).toHaveBeenCalledWith("Zkopírováno",expect.anything());
  const writeText=vi.fn().mockResolvedValue(undefined);vi.stubGlobal("navigator",{clipboard:{writeText}});exec.mockReturnValue(false);
  (document.querySelector('[data-readonly-copy]') as HTMLElement).click();await flushPromises();
  expect(writeText).toHaveBeenCalledWith("Bezpečný text");
  expect(toast.success).toHaveBeenCalledTimes(2);
  writeText.mockRejectedValue(new Error("denied"));
  (document.querySelector('[data-readonly-copy]') as HTMLElement).click();await flushPromises();
  expect(toast.error).toHaveBeenCalledWith("Text se nepodařilo zkopírovat.");
});

it("Alt+F10 takes the keyboard into the bar, arrows move, Esc hides it and returns the focus",async()=>{
  const origin=document.createElement("button");document.body.append(origin);origin.focus();
  view=mount(ReadonlySelectionActions,{props:{enabled:true,selection:{text:"Bezpečný text",anchorRect:new DOMRect(200,200,160,20),sectionHref:"#/pages/p?heading=h"}},attachTo:document.body});await flushPromises();
  expect(document.activeElement).toBe(origin);
  key(origin,"F10",{altKey:true});
  expect(focused()).toBe("Kopírovat");
  key(document.activeElement!,"ArrowRight");expect(focused()).toBe("Zeptat se");
  key(document.activeElement!,"ArrowLeft");key(document.activeElement!,"ArrowLeft");expect(focused()).toBe("Kde se to ještě zmiňuje");
  key(document.activeElement!,"Home");expect(focused()).toBe("Kopírovat");
  key(document.activeElement!,"Escape");await flushPromises();
  expect(view.emitted("close")).toBeTruthy();
  expect(document.activeElement).toBe(origin);
});

it("leaves Alt+F10 alone while the bar is hidden",async()=>{
  view=mount(ReadonlySelectionActions,{props:{enabled:false,selection:{text:"Bezpečný text",anchorRect:new DOMRect(200,200,160,20)}},attachTo:document.body});await flushPromises();
  const event=new KeyboardEvent("keydown",{key:"F10",altKey:true,bubbles:true,cancelable:true});document.body.dispatchEvent(event);
  expect(event.defaultPrevented).toBe(false);
});
