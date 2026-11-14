import {afterEach,expect,it,vi} from "vitest";
import {mount,flushPromises,type VueWrapper} from "@vue/test-utils";
vi.mock("../../ai/interactions",()=>({reportAiInteraction:vi.fn()}));
import TagSuggestions from "./TagSuggestions.vue";
let view:VueWrapper;afterEach(()=>view?.unmount());const result={tags:[{id:"t1",name:"Návody"}],provenance:{location:"company" as const,provider:"Test",model:"test"}};
it("adds only a proposed existing tag on explicit use",async()=>{view=mount(TagSuggestions,{props:{contextKey:"p:1",request:vi.fn().mockResolvedValue(result)}});await view.get('[data-tag-suggest]').trigger('click');await flushPromises();expect(view.emitted('apply')).toBeUndefined();await view.get('[data-suggestion-apply]').trigger('click');expect(view.emitted('apply')?.[0]).toEqual([result.tags]);});
it("rejects stale suggestions and erases model data when disabled",async()=>{view=mount(TagSuggestions,{props:{contextKey:"p:1",request:vi.fn().mockResolvedValue(result)}});await view.get('[data-tag-suggest]').trigger('click');await flushPromises();await view.setProps({contextKey:"p:2"});expect(view.get('[data-suggestion-apply]').attributes('disabled')).toBeDefined();await view.setProps({request:undefined});expect(view.find('.ai-glyph').exists()).toBe(false);expect(view.text()).not.toContain('Návody');});
