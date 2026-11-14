import {api} from "../api";
import type {AiProvenanceView} from "./assistantApi";
import type {PageTag} from "../pageTags";
export interface WritingTextProposal {text:string;provenance:AiProvenanceView;}
export interface ChangeSummaryProposal extends WritingTextProposal {contextKey:string;}
export interface SpellingIssue {spanStart:number;spanEnd:number;original:string;correction:string;}
const request=(body:unknown,signal?:AbortSignal):RequestInit=>({method:"POST",body:JSON.stringify(body),signal});
export function proposeChangeSummary(pageId:string,expectedVersion:number,signal?:AbortSignal){return api<WritingTextProposal>("/assist/change-summary",request({pageId,expectedVersion},signal));}
export function checkSpelling(pageId:string,expectedVersion:number,signal?:AbortSignal){return api<{issues:SpellingIssue[];provenance:AiProvenanceView}>("/assist/publish-check",request({pageId,expectedVersion},signal));}
export function proposeTags(pageId:string,expectedVersion:number,signal?:AbortSignal){return api<{tags:PageTag[];provenance:AiProvenanceView}>("/assist/tags",request({pageId,expectedVersion},signal));}
