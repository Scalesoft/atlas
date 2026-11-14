// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";
import { createEditorUploads } from "./editorUploads";
import { clearCsrf } from "./api";
afterEach(() => { vi.unstubAllGlobals(); clearCsrf(); });
describe("editor upload boundary", () => {
  it("uploads actual multipart with CSRF and leaves content-type boundary to the browser", async () => {
    const fetch = vi.fn().mockResolvedValueOnce(Response.json({token:"csrf-fixture"})).mockResolvedValueOnce(Response.json({id:"id",name:"diagram.png",size:4,mimeType:"image/png",url:"/api/attachments/id/content",downloadUrl:"/api/attachments/id/download"}));
    vi.stubGlobal("fetch",fetch); const uploads=createEditorUploads(()=>({pageId:"page",canWrite:true})); const file=new File(["data"],"diagram.png",{type:"image/png"}); const signal=new AbortController().signal;
    expect(await uploads.image(file,{signal})).toEqual({url:"/api/attachments/id/content",alt:"diagram.png"});
    const [path,options]=fetch.mock.calls[1]; expect(path).toBe("/api/pages/page/attachments"); expect(options.body).toBeInstanceOf(FormData); expect(options.body.get("file")).toBe(file); expect(options.body.get("kind")).toBe("image"); expect(options.headers.get("X-CSRF-TOKEN")).toBe("csrf-fixture"); expect(options.headers.has("Content-Type")).toBe(false); expect(options.signal).toBe(signal);
  });
  it("never contacts the server for readonly or missing-page upload", async () => {
    const fetch=vi.fn(); vi.stubGlobal("fetch",fetch); const uploads=createEditorUploads(()=>({pageId:"page",canWrite:false}));
    await expect(uploads.attachment.upload(new File(["x"],"file.txt"),{signal:new AbortController().signal})).rejects.toThrow(/upravit/); expect(fetch).not.toHaveBeenCalled();
  });
  it("does not return an insertable result after page or permission changes during upload", async () => {
    let finish!: (response:Response)=>void;
    const fetch=vi.fn().mockResolvedValueOnce(Response.json({token:"csrf-fixture"})).mockImplementationOnce(()=>new Promise(resolve=>{finish=resolve})); vi.stubGlobal("fetch",fetch);
    let pageId="original"; const uploads=createEditorUploads(()=>({pageId,canWrite:true})); const pending=uploads.attachment.upload(new File(["x"],"file.txt"),{signal:new AbortController().signal}); await vi.waitFor(()=>expect(fetch).toHaveBeenCalledTimes(2)); pageId="different";
    finish(Response.json({id:"id",name:"file.txt",size:1,mimeType:"text/plain",url:"/api/attachments/id/content",downloadUrl:"/api/attachments/id/download"})); await expect(pending).rejects.toThrow(/změnila/);
  });
  it("uses a download reference for generic attachments and preserves metadata", async () => {
    vi.stubGlobal("fetch",vi.fn().mockResolvedValueOnce(Response.json({token:"csrf-fixture"})).mockResolvedValueOnce(Response.json({id:"id",name:"notes.txt",size:5,mimeType:"text/plain",url:"/api/attachments/id/content",downloadUrl:"/api/attachments/id/download"})));
    const result=await createEditorUploads(()=>({pageId:"page",canWrite:true})).attachment.upload(new File(["notes"],"notes.txt"),{signal:new AbortController().signal}); expect(result).toEqual({url:"/api/attachments/id/download",name:"notes.txt",size:5,mimeType:"text/plain"});
  });
});
