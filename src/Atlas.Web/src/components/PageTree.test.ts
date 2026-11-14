import { afterEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import PageTree from "./PageTree.vue";
import type { TreePage } from "../treeMove";
function node(id:string,parentId:string|null=null):TreePage {return {id,parentId,spaceId:"space",title:id,version:1,isPublished:true,canEdit:true};}
describe("indexed navigation tree",()=>{
  it('shows a custom colored icon only on decorated pages, preserving collapse and link names', async () => {
    const app = mount(PageTree, { props: { pages: [{ ...node('parent'), decoration: { revisionId: 'r1', icon: 'folder', color: 'violet' } }, node('child', 'parent')] } });
    expect(app.get('.tree-page .page-decoration-icon').attributes('data-color')).toBe('violet');
    expect(app.find('.tree-page [data-icon="folder"]').exists()).toBe(true);
    expect(app.findAll('.page-decoration-icon')).toHaveLength(1);
    expect(app.get('.tree-page').text()).toBe('parent');
    await app.get('[aria-label="Sbalit parent"]').trigger('click');
    expect(app.emitted('toggleCollapsed')).toEqual([['parent']]);
    app.unmount();
  });
  it("renders page links and only delegates plain primary-button navigation",async()=>{
    const app=mount(PageTree,{props:{pages:[node("page-one")]}});
    const link=app.get(".tree-page");
    expect(link.element.tagName).toBe("A");
    expect(link.attributes("href")).toContain("#/pages/page-one");

    await link.trigger("click",{ctrlKey:true});
    await link.trigger("click",{button:1});
    expect(app.emitted("select")).toBeUndefined();

    const plainClick=new MouseEvent("click",{bubbles:true,cancelable:true,button:0});
    expect(link.element.dispatchEvent(plainClick)).toBe(false);
    expect(app.emitted("select")).toEqual([[expect.objectContaining({id:"page-one"})]]);
    app.unmount();
  });
  it("uses the host-owned collapsed set and delegates toggles",async()=>{
    const pages=[node("parent"),node("child","parent")];
    const app=mount(PageTree,{props:{pages,collapsedIds:new Set(["parent"])}});
    expect(app.findAll(".tree-page").some(button=>button.text()==="child")).toBe(false);
    await app.get('[aria-label="Rozbalit parent"]').trigger("click");
    expect(app.emitted("toggleCollapsed")?.[0]).toEqual(["parent"]);
    app.unmount();
  });
  it("renders leaf nodes without empty subtrees and keeps user collapse state on refresh",async()=>{
    const pages=[node("parent"),node("child","parent"),...Array.from({length:100},(_,i)=>node(`leaf-${i}`))];
    const collapsedIds=new Set<string>();
    const app=mount(PageTree,{props:{pages,canReorder:true,collapsedIds}});
    expect(app.findAll(".page-tree")).toHaveLength(2);
    await app.get('[aria-label="Sbalit parent"]').trigger("click");
    collapsedIds.add("parent"); await app.setProps({collapsedIds:new Set(collapsedIds)});
    await app.setProps({pages:[...pages,node("another")]});
    expect(app.findAll(".page-tree")).toHaveLength(1);
    expect(app.findAll(".tree-page").some(button=>button.text()==="child")).toBe(false);
    app.unmount();
  });
  it("keeps levels beyond 32 reachable while clamping only their visual indentation",()=>{
    const pages=Array.from({length:40},(_,i)=>node(`level-${i}`,i?`level-${i-1}`:null));
    const app=mount(PageTree,{props:{pages,canReorder:false}});
    expect(app.findAll(".tree-page")).toHaveLength(40);
    // A reader's rows are no moves but can still be dragged out as a copy (a reference, a card).
    expect(app.findAll('.tree-row[draggable="true"]')).toHaveLength(40);
    // The first eight nested lists each add one guide line, so tree levels 2-9 get their own
    // indent column and level 10 onward shares the ninth; deeper levels stay reachable without
    // growing further. The cap moved from six nested levels to eight because the step shrank
    // from 16px to 12px, so eight now cost less width than six used to.
    expect(app.findAll(".page-tree-nested")).toHaveLength(8);
    // A level is an indent inside rows that are as wide as the tree: every nested list carries its depth for the
    // styles, and the depth stops at the ninth column with the guide lines.
    const depths=app.findAll("ul").map(list=>(list.element as HTMLElement).style.getPropertyValue("--tree-depth"));
    expect(depths.slice(0,10)).toEqual(["","1","2","3","4","5","6","7","8","8"]);
    expect(new Set(depths.slice(9))).toEqual(new Set(["8"]));
    app.unmount();
  });
  // Reverses the "always renders a page icon" decision from 0a1c355: the glyph was identical
  // on every row, so it distinguished nothing while costing 24px of a 229px column. The
  // chevron column stays reserved on every row so titles keep one left edge at each depth.
  it("reserves a chevron column for every row without adding decorative icons",()=>{
    const pages=[node("parent"),node("child","parent")];
    const app=mount(PageTree,{props:{pages}});
    const parentRow=app.findAll(".tree-row")[0]!;
    expect(parentRow.find(".tree-toggle").exists()).toBe(true);
    expect(parentRow.find(".tree-toggle-col").exists()).toBe(true);
    expect(parentRow.findAll("svg")).toHaveLength(1);
    const leafRow=app.findAll(".tree-row")[1]!;
    expect(leafRow.find(".tree-toggle").exists()).toBe(false);
    expect(leafRow.find(".tree-toggle-col").exists()).toBe(true);
    expect(leafRow.findAll("svg")).toHaveLength(0);
    app.unmount();
  });
  it("opens the page from anywhere in its row, not only from the link in it",async()=>{
    // The row is as wide as the tree and lights up as a whole: its indent and the empty arrow column answer too.
    const pages=[node("parent"),node("child","parent")];
    const app=mount(PageTree,{props:{pages}});
    const [parentRow,childRow]=app.findAll(".tree-row");
    await childRow!.trigger("click");
    await childRow!.get(".tree-toggle-col").trigger("click");
    expect(app.emitted("select")!.map(([page])=>(page as {id:string}).id)).toEqual(["child","child"]);
    // The arrow only folds, and the link reports its own click once.
    await parentRow!.get(".tree-toggle").trigger("click");
    await parentRow!.get(".tree-page").trigger("click");
    expect(app.emitted("select")!.map(([page])=>(page as {id:string}).id)).toEqual(["child","child","parent"]);
    // A click meant for a new tab or window is left alone.
    await childRow!.trigger("click",{ctrlKey:true});
    expect(app.emitted("select")).toHaveLength(3);
    app.unmount();
  });
  it("gives every link the full title, never truncated in markup",()=>{
    const app=mount(PageTree,{props:{pages:[node("A very long page title that could visually clip in a narrow sidebar column")]}});
    const link=app.get(".tree-page");
    // The full title is the link's own text, so its accessible name is complete
    // even when CSS clips it; the hover hint that used to be a native `title` is
    // now a Tooltip, which mounts nothing until it opens.
    expect(link.text()).toContain("A very long page title that could visually clip in a narrow sidebar column");
    expect(link.attributes("title")).toBeUndefined();
    app.unmount();
  });
  it("marks a draft accessibly without a badge taking space in the row",()=>{
    const app=mount(PageTree,{props:{pages:[node("published"),{...node("draft"),isPublished:false}]}});
    const rows=app.findAll(".tree-row");
    expect(rows[0]!.find("[data-slot=badge]").exists()).toBe(false);
    expect(rows[1]!.find("[data-slot=badge]").exists()).toBe(false);
    expect(rows[1]!.get(".sr-only").text()).toBe("— Koncept");
    expect(rows[1]!.get(".tree-page-title").classes()).toContain("tree-page-draft");
    expect(rows[1]!.get(".tree-page-title").text()).toBe("draft");
    expect(app.find(".draft-dot").exists()).toBe(false);
    app.unmount();
  });
});

// A branch folds into its row and unfolds under it. One with hundreds of rows reaches far below the window: eased over
// all of it, the part in view filled within a frame or two (unfolding) or stood empty until the end (folding).
describe("a branch folding and unfolding",()=>{
  afterEach(()=>{ delete (Element.prototype as Partial<Element>).animate; vi.restoreAllMocks(); });
  function recordAnimations() {
    const started:{frames:Keyframe[],options:KeyframeAnimationOptions,animation:{cancel:ReturnType<typeof vi.fn>,playState:string}}[]=[];
    Element.prototype.animate=function(this:Element,frames:Keyframe[],options:KeyframeAnimationOptions){
      const animation={finished:new Promise<void>(()=>{}),finish:vi.fn(),cancel:vi.fn(),playState:"running"};
      started.push({frames,options,animation});
      return animation as unknown as Animation;
    } as unknown as Element["animate"];
    return started;
  }
  const pages=[node("parent"),node("child","parent"),node("after")];
  it("moves only the part in view when its own chevron asks, and the whole branch when a page is revealed from elsewhere",async()=>{
    const started=recordAnimations();
    vi.spyOn(Element.prototype,"getBoundingClientRect").mockReturnValue({top:100,height:5000} as DOMRect);
    vi.spyOn(window,"innerHeight","get").mockReturnValue(768);
    // The shell answers the chevron at once, as App does.
    const app=mount(PageTree,{props:{pages,collapsedIds:new Set<string>(),onToggleCollapsed:(id:string)=>{ const next=new Set(app.props("collapsedIds")); if(!next.delete(id)) next.add(id); void app.setProps({collapsedIds:next}); }},attachTo:document.body});
    await app.get('[aria-label="Sbalit parent"]').trigger("click");
    expect(started.at(-1)!.frames[0]).toMatchObject({height:"668px"});
    expect(started.at(-1)!.frames[1]).toMatchObject({height:"0px"});
    for(const {animation} of started) animation.playState="finished";
    // Revealed from elsewhere (a page opened in it), it unfolds whole: the list follows it down to that page's row.
    await app.setProps({collapsedIds:new Set<string>()});
    await new Promise(resolve=>setTimeout(resolve));
    expect(started.at(-1)!.frames[1]).toMatchObject({height:"5000px"});
    app.unmount();
  });
  it("turns around from the height it has reached, to the fraction of a pixel",async()=>{
    const started=recordAnimations();
    const app=mount(PageTree,{props:{pages,collapsedIds:new Set<string>()},attachTo:document.body});
    await app.setProps({collapsedIds:new Set(["parent"])});
    expect(started).toHaveLength(1);
    vi.spyOn(Element.prototype,"getBoundingClientRect").mockReturnValue({top:100,height:41.6} as DOMRect);
    await app.setProps({collapsedIds:new Set<string>()});
    expect(started[0]!.animation.cancel).toHaveBeenCalledTimes(1);
    expect(started[1]!.frames[0]).toMatchObject({height:"41.6px"});
    app.unmount();
  });
  it("takes longer the further the rows below have to go, and leaves from rest",async()=>{
    // On the token alone a branch of 400 px covered a third of its way in the first frame.
    const started=recordAnimations();
    let height=40;
    vi.spyOn(Element.prototype,"getBoundingClientRect").mockImplementation(()=>({top:100,height} as DOMRect));
    const app=mount(PageTree,{props:{pages,collapsedIds:new Set<string>()},attachTo:document.body});
    await app.setProps({collapsedIds:new Set(["parent"])});
    for(const {animation} of started) animation.playState="finished";
    height=400;
    await app.setProps({collapsedIds:new Set<string>()});
    await new Promise(resolve=>setTimeout(resolve));
    // A fold of 40 px: the shortest travel, and shorter still because it leaves. An unfolding of 400 px: the 1.25× cap, 250 ms.
    expect(started.map(({options})=>Math.round(Number(options.duration)))).toEqual([187,250]);
    expect(started.every(({options})=>options.easing==="cubic-bezier(.3, .2, .1, 1)")).toBe(true);
    app.unmount();
  });
});

describe("dragging a page out of the tree",()=>{
  /** A dragstart on the row with a stand-in for the browser's DataTransfer; `prevented` is whether anybody cancelled the drag. */
  async function dragStart(props:Record<string,unknown>,page:TreePage,onDragStart?:(page:TreePage,event:DragEvent)=>void){
    const app=mount(PageTree,{props:{pages:[page],...props,...(onDragStart?{onDragStart}:{})}});
    const dataTransfer={setData:vi.fn(),effectAllowed:""};
    // Vue skips a listener of an event that an earlier one (the tree's capture handler) already stamped within the millisecond the
    // listeners were attached; a real drag starts later than that.
    await new Promise(resolve=>setTimeout(resolve,5));
    const event=new Event("dragstart",{bubbles:true,cancelable:true});
    Object.assign(event,{dataTransfer});
    const notPrevented=app.get(".tree-row").element.dispatchEvent(event);
    const emitted=app.emitted("dragStart")?.length??0;
    app.unmount();
    return {dataTransfer,prevented:!notPrevented,emitted};
  }
  it("starts a reordering drag that also carries the page for a drop elsewhere, e.g. a board",async()=>{
    const {dataTransfer,prevented,emitted}=await dragStart({canReorder:true},node("page-one"));
    expect(prevented).toBe(false);
    expect(emitted).toBe(1);
    expect(dataTransfer.setData).toHaveBeenCalledWith("application/x-atlas-page","page-one");
    // The tree moves it; a board copies it: both drops are allowed.
    expect(dataTransfer.effectAllowed).toBe("copyMove");
  });
  it.each([
    ["a tree the reader cannot reorder",{canReorder:false},node("page-one")],
    ["a page the reader cannot edit",{canReorder:true},{...node("page-one"),canEdit:false}],
    ["a tree that is busy",{canReorder:true,busy:true},node("page-one")],
  ])("lets %s be dragged as a copy, without a reordering state",async(_,props,page)=>{
    const {dataTransfer,prevented,emitted}=await dragStart(props,page);
    expect(prevented).toBe(false);
    expect(emitted).toBe(0);
    expect(dataTransfer.setData).toHaveBeenCalledWith("application/x-atlas-page","page-one");
    expect(dataTransfer.effectAllowed).toBe("copy");
  });
  it("leaves a drag the host cancelled without a payload",async()=>{
    const {dataTransfer,prevented}=await dragStart({canReorder:true},node("page-one"),(_,event)=>event.preventDefault());
    expect(prevented).toBe(true);
    expect(dataTransfer.setData).not.toHaveBeenCalled();
  });
  it("leaves Shift + right-click to the browser's own menu",async()=>{
    const app=mount(PageTree,{props:{pages:[node("page-one")],menu:true}});
    const row=app.get(".tree-row").element;
    const plain=new MouseEvent("contextmenu",{bubbles:true,cancelable:true});
    row.dispatchEvent(plain);
    expect(plain.defaultPrevented).toBe(true);
    const shifted=new MouseEvent("contextmenu",{bubbles:true,cancelable:true,shiftKey:true});
    row.dispatchEvent(shifted);
    expect(shifted.defaultPrevented).toBe(false);
    app.unmount();
  });
});

