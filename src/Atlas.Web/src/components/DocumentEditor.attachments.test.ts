import { afterEach, expect, it, vi } from "vitest";
import { mount, flushPromises, type VueWrapper } from "@vue/test-utils";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import DocumentEditor from "./DocumentEditor.vue";
let wrapper:VueWrapper|undefined;
afterEach(()=>{wrapper?.unmount();vi.unstubAllGlobals()});
it("navigates a persisted heading inside the current readonly document without editing its content",async()=>{
  const value:MarkdownRichValue={format:"atlas-rich",version:1,plugins:[{id:"heading-anchors",version:1}],document:{type:"doc",content:[{type:"heading",attrs:{level:2,headingId:"section-id"},content:[{type:"text",text:"Sekce"}]}]}};
  wrapper=mount(DocumentEditor,{props:{value,readonly:true,dark:false,accent:"blue",pageId:"page",spaceId:"space"},attachTo:document.body});await flushPromises();
  const exposed=wrapper.vm as unknown as {navigateToHeading:(id:string)=>boolean;capture:()=>{contentJson:string}};
  const before=exposed.capture().contentJson;expect(exposed.navigateToHeading("section-id")).toBe(true);expect(exposed.navigateToHeading("missing")).toBe(false);expect(document.activeElement).toBe(wrapper.get("h2").element);expect(exposed.capture().contentJson).toBe(before);
});
it("the page anchor finds a requirement heading by its id and by its former block id, so the deployment overview opens the page there",async()=>{
  // 3.6: a requirement is a heading; the overview links its headingId, an older link the former block id the heading keeps as an alias.
  const former="6f1c1d3e-8a4b-4c55-9d1e-000000000001";
  const value:MarkdownRichValue={format:"atlas-rich",version:1,plugins:[{id:"heading-anchors",version:2},{id:"atlas-requirements",version:3}],document:{type:"doc",content:[{type:"paragraph",content:[{type:"text",text:"Úvod"}]},{type:"heading",attrs:{level:3,headingId:"portal.ssl",aliases:former,state:"planned"},content:[{type:"text",text:"SSL certifikát"}]},{type:"paragraph",content:[{type:"text",text:"Poznámka."}]}]}} as MarkdownRichValue;
  wrapper=mount(DocumentEditor,{props:{value,readonly:true,dark:false,accent:"blue",pageId:"page",spaceId:"space"},attachTo:document.body});await flushPromises();
  const exposed=wrapper.vm as unknown as {navigateToHeading:(id:string)=>boolean};
  for(const anchor of ["portal.ssl",former]){
    expect(exposed.navigateToHeading(anchor)).toBe(true);
    expect((document.activeElement as HTMLElement).textContent).toContain("SSL certifikát");
    (document.activeElement as HTMLElement).blur();
  }
  expect(exposed.navigateToHeading("6f1c1d3e-8a4b-4c55-9d1e-000000000009")).toBe(false);
});
it("captures the read document with its Markdown projection",async()=>{
  const value:MarkdownRichValue={format:"atlas-rich",version:1,document:{type:"doc",content:[{type:"heading",attrs:{level:2},content:[{type:"text",text:"Nadpis"}]},{type:"paragraph",content:[{type:"text",text:"Obsah ke stažení"}]}]}};
  wrapper=mount(DocumentEditor,{props:{value,readonly:true,dark:false,accent:"blue",pageId:"page",spaceId:"space"}});await flushPromises();
  const captured=(wrapper.vm as unknown as {capture:()=>{contentJson:string;markdown:string}}).capture();
  expect(JSON.parse(captured.contentJson).document.content).toHaveLength(2);expect(captured.markdown).toContain("## Nadpis");expect(captured.markdown).toContain("Obsah ke stažení");
});
it("reopens and exports actual attachment and image content while readonly without upload or collaboration calls",async()=>{
  const fetch=vi.fn(); vi.stubGlobal("fetch",fetch);
  const data={version:1,name:"Notes.txt",url:"/api/attachments/file/download",size:12,mimeType:"text/plain"};
  const value:MarkdownRichValue={format:"atlas-rich",version:1,plugins:[{id:"attachments",version:1},{id:"youtrack",version:1}],document:{type:"doc",content:[{type:"attachment",attrs:{data}},{type:"image",attrs:{src:"/api/attachments/image/content",alt:"Diagram"}}]}};
  wrapper=mount(DocumentEditor,{props:{value,readonly:true,dark:false,accent:"blue",pageId:"page",spaceId:"space"}});await flushPromises();
  expect(wrapper.find('[role="alert"]').exists()).toBe(false);expect(wrapper.get(".tiptap").attributes("contenteditable")).toBe("false");
  const captured=JSON.parse((wrapper.vm as unknown as {capture:()=>{contentJson:string}}).capture().contentJson);
  expect(captured.document.content[0]).toEqual({type:"attachment",attrs:{data}});expect(JSON.stringify(captured.document)).toContain("/api/attachments/image/content");expect(fetch.mock.calls.map(([url])=>String(url)).filter(url=>!url.endsWith("/lexicon"))).toEqual([]);
  await wrapper.setProps({dark:true,accent:"green"});expect(JSON.parse((wrapper.vm as unknown as {capture:()=>{contentJson:string}}).capture().contentJson).document).toEqual(captured.document);
});
