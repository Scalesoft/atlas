import {api} from "../api";
/** Anonymous aggregate only. Never awaits the write, retries it, or includes the proposal text or identity. */
export function reportAiInteraction(task:string,kind:string,outcome:"shown"|"applied"|"dismissed"|"up"|"down") { void api<void>("/ai/interactions",{method:"POST",body:JSON.stringify({task,kind,outcome})}).catch(()=>{}); }
