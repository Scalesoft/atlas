"""Batch 1, the approved pilot (2026-10-09): one motif per area, hand-placed paths."""

C = {}
M = {'it-code': ('Vývoj a kód', 'IT', 'kód, repozitář, editor, terminál, programování', 27), 'it-database': ('Databáze a data', 'IT', 'databáze, SQL, tabulky, dotazy, reporting', 302), 'it-security': ('Bezpečnost', 'IT', 'zabezpečení, přístupy, hesla, audit, GDPR', 255), 'it-cicd': ('CI/CD a nasazení', 'IT', 'build, pipeline, release, nasazení, git', 152), 'it-monitoring': ('Monitoring a provoz', 'IT', 'dohled, metriky, alerty, incidenty, SLA', 80), 'it-api': ('API a integrace', 'IT', 'rozhraní, integrace, webhooky, služby', 200), 'ops-finance': ('Finance a účetnictví', 'Firma', 'faktury, rozpočet, náklady, výkazy', 130), 'ops-sales': ('Obchod a klienti', 'Firma', 'obchod, CRM, nabídky, cíle, klienti', 40), 'ops-marketing': ('Marketing', 'Firma', 'kampaně, sociální sítě, web, PR', 340), 'ops-legal': ('Právo a smlouvy', 'Firma', 'smlouvy, podpisy, směrnice, compliance', 280), 'ops-project': ('Projekty a plánování', 'Firma', 'kanban, harmonogram, úkoly, sprinty', 230), 'ops-support': ('Podpora a helpdesk', 'Firma', 'zákaznická péče, tikety, chat, FAQ', 182), 'ops-logistics': ('Logistika a doprava', 'Firma', 'doprava, rozvoz, trasy, zásilky, dodavatelé', 60), 'dom-health': ('Zdravotnictví', 'Obor', 'pacienti, péče, telemedicína, klinika', 10), 'gen-ideas': ('Nápady a sandbox', 'Obecné', 'nápady, brainstorming, pokusy, poznámky', 100)}

C['it-code'] = (True, '''
<rect x="250" y="22" width="330" height="176" rx="12" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<rect x="291" y="122" width="288" height="16" fill="#f2f2f2"/>
<path d="M250 50h330" stroke="#3a3a3a" stroke-width="2"/>
<path d="M290 51v146" stroke="#dedede" stroke-width="2"/>
<circle cx="268" cy="36" r="4" fill="#5a5a5a"/><circle cx="282" cy="36" r="4" fill="#9a9a9a"/><circle cx="296" cy="36" r="4" fill="#dedede"/>
<g fill="#bdbdbd"><rect x="264" y="62" width="14" height="5" rx="2"/><rect x="264" y="78" width="14" height="5" rx="2"/><rect x="264" y="94" width="14" height="5" rx="2"/><rect x="264" y="110" width="14" height="5" rx="2"/><rect x="264" y="127" width="14" height="5" rx="2"/><rect x="264" y="142" width="14" height="5" rx="2"/><rect x="264" y="158" width="14" height="5" rx="2"/><rect x="264" y="174" width="14" height="5" rx="2"/></g>
<rect x="302" y="60" width="60" height="8" rx="4" fill="#5a5a5a"/><rect x="370" y="60" width="90" height="8" rx="4" fill="#bdbdbd"/>
<rect x="318" y="76" width="40" height="8" rx="4" fill="#9a9a9a"/><rect x="366" y="76" width="120" height="8" rx="4" fill="#bdbdbd"/>
<rect x="318" y="92" width="80" height="8" rx="4" fill="#5a5a5a"/><rect x="406" y="92" width="50" height="8" rx="4" fill="#9a9a9a"/>
<rect x="334" y="108" width="140" height="8" rx="4" fill="#bdbdbd"/>
<rect x="334" y="126" width="70" height="8" rx="4" fill="#9a9a9a"/><rect x="412" y="126" width="60" height="8" rx="4" fill="#5a5a5a"/>
<rect x="318" y="142" width="30" height="8" rx="4" fill="#5a5a5a"/>
<rect x="302" y="158" width="20" height="8" rx="4" fill="#5a5a5a"/><rect x="330" y="158" width="100" height="8" rx="4" fill="#bdbdbd"/>
<rect x="302" y="172" width="3" height="14" fill="#3a3a3a"/>
<rect x="508" y="128" width="104" height="66" rx="8" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
<path d="M522 148l9 7-9 7" fill="none" stroke="#ffffff" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>
<rect x="538" y="160" width="26" height="3" rx="1.5" fill="#ffffff"/>
<circle cx="130" cy="96" r="58" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/>
<path d="M112 76l-22 20 22 20M148 76l22 20-22 20M138 66l-16 60" fill="none" stroke="#3a3a3a" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>
''')

C['it-database'] = (True, '''
<path d="M230 100h90" fill="none" stroke="#8a8a8a" stroke-width="2" stroke-dasharray="6 6"/>
<path d="M314 93l8 7-8 7" fill="none" stroke="#3a3a3a" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
<path d="M330 40v120a70 16 0 0 0 140 0V40" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<path d="M330 80a70 16 0 0 0 140 0M330 120a70 16 0 0 0 140 0" fill="none" stroke="#3a3a3a" stroke-width="2"/>
<ellipse cx="400" cy="40" rx="70" ry="16" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/>
<g fill="#5a5a5a"><circle cx="352" cy="74" r="3.5"/><circle cx="352" cy="114" r="3.5"/><circle cx="352" cy="154" r="3.5"/></g>
<g fill="#bdbdbd"><rect x="364" y="72" width="44" height="5" rx="2.5"/><rect x="364" y="112" width="30" height="5" rx="2.5"/><rect x="364" y="152" width="52" height="5" rx="2.5"/></g>
<rect x="60" y="40" width="170" height="120" rx="8" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<path d="M60 64V48a8 8 0 0 1 8-8h154a8 8 0 0 1 8 8v16z" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
<path d="M61 88h168M61 112h168M61 136h168M110 65v94M170 65v94" stroke="#dedede" stroke-width="2"/>
<g fill="#bdbdbd"><rect x="72" y="73" width="26" height="6" rx="3"/><rect x="122" y="73" width="36" height="6" rx="3"/><rect x="182" y="73" width="30" height="6" rx="3"/><rect x="72" y="97" width="22" height="6" rx="3"/><rect x="122" y="97" width="28" height="6" rx="3" fill="#5a5a5a"/><rect x="182" y="97" width="36" height="6" rx="3"/><rect x="72" y="121" width="28" height="6" rx="3"/><rect x="122" y="121" width="40" height="6" rx="3"/><rect x="182" y="121" width="22" height="6" rx="3" fill="#9a9a9a"/><rect x="72" y="145" width="20" height="6" rx="3"/><rect x="122" y="145" width="30" height="6" rx="3"/><rect x="182" y="145" width="32" height="6" rx="3"/></g>
<circle cx="540" cy="96" r="28" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/>
<path d="M560 116l26 26" stroke="#3a3a3a" stroke-width="6" stroke-linecap="round"/>
<path d="M526 90h28M526 102h18" stroke="#9a9a9a" stroke-width="3" stroke-linecap="round"/>
''')

C['it-security'] = (False, '''
<path d="M342 110h-60v-40h-60M342 140h-90v30h-70M498 90h50v-40h40M498 130h70v40h30" fill="none" stroke="#9a9a9a" stroke-width="2"/>
<g fill="#ffffff" stroke="#3a3a3a" stroke-width="2"><circle cx="222" cy="70" r="5"/><circle cx="182" cy="170" r="5"/><circle cx="588" cy="50" r="5"/><circle cx="598" cy="170" r="5"/></g>
<path d="M420 20l78 26v56c0 52-34 86-78 100-44-14-78-48-78-100V46z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/>
<path d="M420 40l60 20v42c0 40-26 66-60 78-34-12-60-38-60-78V60z" fill="#ffffff" stroke="#9a9a9a" stroke-width="2"/>
<path d="M406 102V88a14 14 0 0 1 28 0v14" fill="none" stroke="#3a3a3a" stroke-width="5"/>
<rect x="394" y="100" width="52" height="42" rx="7" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
<circle cx="420" cy="116" r="5" fill="#ffffff"/><rect x="418" y="118" width="4" height="12" rx="2" fill="#ffffff"/>
<rect x="40" y="40" width="150" height="40" rx="10" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<g fill="#3a3a3a"><circle cx="60" cy="60" r="4"/><circle cx="76" cy="60" r="4"/><circle cx="92" cy="60" r="4"/><circle cx="108" cy="60" r="4"/><circle cx="124" cy="60" r="4"/></g>
<circle cx="170" cy="60" r="10" fill="#5a5a5a"/>
<path d="M165 60l4 4 7-8" fill="none" stroke="#ffffff" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>
<path d="M60 104h90M60 120h60" stroke="#bdbdbd" stroke-width="2" stroke-linecap="round"/>
''')

C['it-cicd'] = (True, '''
<path d="M30 160h90M60 160c0-36 14-50 44-50h36" fill="none" stroke="#3a3a3a" stroke-width="2.5"/>
<g fill="#ffffff" stroke="#3a3a3a" stroke-width="2"><circle cx="40" cy="160" r="7"/><circle cx="84" cy="160" r="7"/><circle cx="120" cy="160" r="7" fill="#5a5a5a"/><circle cx="106" cy="110" r="7"/></g>
<g fill="#ffffff" stroke="#3a3a3a" stroke-width="2">
<rect x="140" y="70" width="90" height="60" rx="10"/><rect x="255" y="70" width="90" height="60" rx="10"/><rect x="370" y="70" width="90" height="60" rx="10"/><rect x="485" y="70" width="90" height="60" rx="10" stroke-dasharray="6 5"/>
</g>
<path d="M232 100h20M347 100h20M462 100h20" stroke="#3a3a3a" stroke-width="2"/>
<path d="M246 94l6 6-6 6M361 94l6 6-6 6M476 94l6 6-6 6" fill="none" stroke="#3a3a3a" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
<path d="M185 82v6M185 112v6M167 100h6M197 100h6M172 87l4 4M194 109l4 4M198 87l-4 4M172 113l4-4" stroke="#3a3a3a" stroke-width="3.5" stroke-linecap="round"/>
<circle cx="185" cy="100" r="11" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/><circle cx="185" cy="100" r="4" fill="#3a3a3a"/>
<path d="M282 86l3 3 5-6M282 100l3 3 5-6M282 114l3 3 5-6" fill="none" stroke="#3a3a3a" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
<g fill="#bdbdbd"><rect x="296" y="83" width="30" height="5" rx="2.5"/><rect x="296" y="97" width="22" height="5" rx="2.5"/><rect x="296" y="111" width="26" height="5" rx="2.5"/></g>
<path d="M415 82l18 9v20l-18 9-18-9V91z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M397 91l18 9 18-9M415 100v20" fill="none" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M516 114c-9 0-14-6-14-12 0-7 6-12 12-12 2-8 9-12 16-12 9 0 15 6 16 14 7 0 12 5 12 11s-5 11-12 11z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/>
<path d="M530 108V94M524 100l6-6 6 6" fill="none" stroke="#3a3a3a" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
<g stroke="#3a3a3a" stroke-width="2"><circle cx="226" cy="74" r="11" fill="#5a5a5a"/><circle cx="341" cy="74" r="11" fill="#5a5a5a"/><circle cx="456" cy="74" r="11" fill="#5a5a5a"/></g>
<path d="M221 74l4 4 6-7M336 74l4 4 6-7M451 74l4 4 6-7" fill="none" stroke="#ffffff" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>
<rect x="140" y="156" width="435" height="12" rx="6" fill="#efefef" stroke="#3a3a3a" stroke-width="2"/>
<rect x="140" y="156" width="330" height="12" rx="6" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
''')

C['it-monitoring'] = (False, '''
<rect x="230" y="20" width="380" height="180" rx="12" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<path d="M230 48h380" stroke="#3a3a3a" stroke-width="2"/>
<rect x="246" y="30" width="70" height="8" rx="4" fill="#5a5a5a"/><rect x="324" y="30" width="40" height="8" rx="4" fill="#bdbdbd"/>
<path d="M250 80h220M250 110h220M250 140h220M250 170h220" stroke="#dedede" stroke-width="2"/>
<path d="M250 150L280 140L310 146L340 120L370 128L400 96L430 104L460 70V180H250z" fill="#efefef"/>
<path d="M250 150L280 140L310 146L340 120L370 128L400 96L430 104L460 70" fill="none" stroke="#3a3a3a" stroke-width="2.5" stroke-linejoin="round"/>
<circle cx="460" cy="70" r="6" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
<path d="M490 60v126" stroke="#dedede" stroke-width="2"/>
<path d="M505 116a38 38 0 0 1 76 0" fill="none" stroke="#dedede" stroke-width="10"/>
<path d="M505 116a38 38 0 0 1 64-27" fill="none" stroke="#5a5a5a" stroke-width="10"/>
<path d="M543 116l18-20" stroke="#3a3a3a" stroke-width="3" stroke-linecap="round"/><circle cx="543" cy="116" r="5" fill="#3a3a3a"/>
<rect x="505" y="140" width="76" height="9" rx="4.5" fill="#efefef"/><rect x="505" y="140" width="50" height="9" rx="4.5" fill="#9a9a9a"/>
<rect x="505" y="158" width="76" height="9" rx="4.5" fill="#efefef"/><rect x="505" y="158" width="28" height="9" rx="4.5" fill="#5a5a5a"/>
<path d="M100 84c-6 10-6 22 0 32M160 84c6 10 6 22 0 32M88 74c-12 18-12 34 0 52M172 74c12 18 12 34 0 52" fill="none" stroke="#9a9a9a" stroke-width="2.5" stroke-linecap="round"/>
<path d="M110 122h40l-6-10V94a14 14 0 0 0-28 0v18z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M124 128a6 6 0 0 0 12 0" fill="none" stroke="#3a3a3a" stroke-width="2"/>
<circle cx="130" cy="77" r="3" fill="#3a3a3a"/>
<circle cx="147" cy="84" r="8" fill="#5a5a5a" stroke="#ffffff" stroke-width="2"/>
''')

C['it-api'] = (True, '''
<path d="M40 150c40 0 60-40 100-40h40" fill="none" stroke="#3a3a3a" stroke-width="3" stroke-linecap="round"/>
<rect x="220" y="30" width="220" height="160" rx="12" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<rect x="176" y="96" width="40" height="28" rx="6" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
<path d="M216 104h10M216 116h10" stroke="#3a3a3a" stroke-width="4" stroke-linecap="round"/>
<path d="M246 42c-5 0-7 3-7 7v3c0 3-2 5-5 5 3 0 5 2 5 5v3c0 4 2 7 7 7M266 42c5 0 7 3 7 7v3c0 3 2 5 5 5-3 0-5 2-5 5v3c0 4-2 7-7 7" fill="none" stroke="#3a3a3a" stroke-width="2" stroke-linecap="round"/>
<rect x="290" y="52" width="80" height="8" rx="4" fill="#9a9a9a"/>
<rect x="240" y="88" width="44" height="18" rx="9" fill="#5a5a5a"/><rect x="294" y="93" width="120" height="8" rx="4" fill="#bdbdbd"/>
<rect x="240" y="118" width="44" height="18" rx="9" fill="#9a9a9a"/><rect x="294" y="123" width="96" height="8" rx="4" fill="#bdbdbd"/>
<rect x="240" y="148" width="44" height="18" rx="9" fill="#bdbdbd"/><rect x="294" y="153" width="110" height="8" rx="4" fill="#bdbdbd"/>
<path d="M440 97c40 0 46-37 76-37M440 127h106M440 157c40 0 46 3 76 3" fill="none" stroke="#3a3a3a" stroke-width="2"/>
<g fill="#ffffff" stroke="#3a3a3a" stroke-width="2"><circle cx="440" cy="97" r="4"/><circle cx="440" cy="127" r="4"/><circle cx="440" cy="157" r="4"/></g>
<path d="M562 60l-11 19h-22l-11-19 11-19h22z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M592 127l-11 19h-22l-11-19 11-19h22z" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M562 160l-11 19h-22l-11-19 11-19h22z" fill="#ffffff" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
''')

C['ops-finance'] = (False, '''
<g stroke="#3a3a3a" stroke-width="2">
<path d="M76 150v8a34 8 0 0 0 68 0v-8" fill="#ffffff"/><ellipse cx="110" cy="150" rx="34" ry="8" fill="#f2f2f2"/>
<path d="M76 138v8a34 8 0 0 0 68 0v-8" fill="#ffffff"/><ellipse cx="110" cy="138" rx="34" ry="8" fill="#f2f2f2"/>
<path d="M76 126v8a34 8 0 0 0 68 0v-8" fill="#ffffff"/><ellipse cx="110" cy="126" rx="34" ry="8" fill="#f2f2f2"/>
<path d="M76 114v8a34 8 0 0 0 68 0v-8" fill="#ffffff"/><ellipse cx="110" cy="114" rx="34" ry="8" fill="#f2f2f2"/>
<path d="M76 102v8a34 8 0 0 0 68 0v-8" fill="#ffffff"/><ellipse cx="110" cy="102" rx="34" ry="8" fill="#5a5a5a"/>
<path d="M166 150v8a34 8 0 0 0 68 0v-8" fill="#ffffff"/><ellipse cx="200" cy="150" rx="34" ry="8" fill="#f2f2f2"/>
<path d="M166 138v8a34 8 0 0 0 68 0v-8" fill="#ffffff"/><ellipse cx="200" cy="138" rx="34" ry="8" fill="#9a9a9a"/>
</g>
<rect x="290" y="20" width="160" height="180" rx="8" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<rect x="308" y="38" width="64" height="10" rx="5" fill="#5a5a5a"/><rect x="404" y="38" width="28" height="10" rx="5" fill="#bdbdbd"/>
<g fill="#bdbdbd"><rect x="308" y="66" width="72" height="7" rx="3.5"/><rect x="308" y="84" width="58" height="7" rx="3.5"/><rect x="308" y="102" width="80" height="7" rx="3.5"/><rect x="308" y="120" width="50" height="7" rx="3.5"/></g>
<g fill="#9a9a9a"><rect x="404" y="66" width="28" height="7" rx="3.5"/><rect x="404" y="84" width="28" height="7" rx="3.5"/><rect x="404" y="102" width="28" height="7" rx="3.5"/><rect x="404" y="120" width="28" height="7" rx="3.5"/></g>
<path d="M306 140h128" stroke="#3a3a3a" stroke-width="2"/>
<rect x="308" y="152" width="40" height="10" rx="5" fill="#9a9a9a"/><rect x="392" y="152" width="40" height="10" rx="5" fill="#3a3a3a"/>
<circle cx="440" cy="178" r="20" fill="#ffffff" stroke="#5a5a5a" stroke-width="2.5" stroke-dasharray="5 4"/>
<path d="M431 178l6 6 11-12" fill="none" stroke="#5a5a5a" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/>
<path d="M484 186h110M484 186V48" stroke="#3a3a3a" stroke-width="2" stroke-linecap="round"/>
<g stroke="#3a3a3a" stroke-width="2"><rect x="496" y="126" width="18" height="60" fill="#bdbdbd"/><rect x="522" y="98" width="18" height="88" fill="#bdbdbd"/><rect x="548" y="112" width="18" height="74" fill="#bdbdbd"/><rect x="574" y="72" width="18" height="114" fill="#5a5a5a"/></g>
<path d="M505 110L531 82L557 94L580 56" fill="none" stroke="#3a3a3a" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>
<path d="M568 56h12v12" fill="none" stroke="#3a3a3a" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>
''')

C['ops-sales'] = (False, '''
<rect x="50" y="46" width="170" height="84" rx="10" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/>
<rect x="40" y="36" width="170" height="84" rx="10" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<circle cx="74" cy="70" r="18" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/>
<circle cx="74" cy="65" r="6" fill="#3a3a3a"/><path d="M63 81a11 8 0 0 1 22 0z" fill="#3a3a3a"/>
<rect x="102" y="60" width="80" height="8" rx="4" fill="#5a5a5a"/><rect x="102" y="76" width="56" height="6" rx="3" fill="#bdbdbd"/>
<rect x="56" y="98" width="40" height="10" rx="5" fill="#e6e6e6"/><rect x="102" y="98" width="52" height="10" rx="5" fill="#9a9a9a"/>
<circle cx="470" cy="100" r="74" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<circle cx="470" cy="100" r="52" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/>
<circle cx="470" cy="100" r="30" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<circle cx="470" cy="100" r="11" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
<path d="M262 166L466 102" stroke="#3a3a3a" stroke-width="3" stroke-linecap="round"/>
<path d="M262 166L283 159L274 152L253 158zM262 166L283 159L280 171L259 177z" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="1.5" stroke-linejoin="round"/>
<path d="M300 40l4 10 10 4-10 4-4 10-4-10-10-4 10-4z" fill="#bdbdbd"/>
''')

C['ops-marketing'] = (False, '''
<path d="M120 46l6 16 16 6-16 6-6 16-6-16-16-6 16-6z" fill="#5a5a5a"/>
<path d="M176 32l4 10 10 4-10 4-4 10-4-10-10-4 10-4z" fill="#bdbdbd"/>
<circle cx="72" cy="112" r="5" fill="#9a9a9a"/><circle cx="176" cy="110" r="4" fill="#3a3a3a"/><rect x="146" y="132" width="10" height="10" rx="2" fill="#bdbdbd" transform="rotate(20 151 137)"/>
<path d="M276 132l10 40h20l-6-34" fill="#ffffff" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M256 88l124-50v144l-124-50z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<rect x="226" y="84" width="32" height="52" rx="7" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
<ellipse cx="380" cy="110" rx="14" ry="72" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<path d="M410 74c14 22 14 50 0 72M430 56c24 34 24 74 0 108" fill="none" stroke="#9a9a9a" stroke-width="3" stroke-linecap="round"/>
<rect x="466" y="30" width="118" height="66" rx="10" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<rect x="476" y="40" width="44" height="46" rx="6" fill="#e6e6e6"/>
<path d="M476 80l14-16 10 10 8-6 12 12v6a6 6 0 0 1-6 6h-32a6 6 0 0 1-6-6z" fill="#9a9a9a"/>
<rect x="530" y="44" width="44" height="7" rx="3.5" fill="#5a5a5a"/><rect x="530" y="58" width="34" height="6" rx="3" fill="#bdbdbd"/><rect x="530" y="70" width="40" height="6" rx="3" fill="#bdbdbd"/>
<rect x="480" y="116" width="112" height="56" rx="10" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/>
<path d="M506 152c-8-6-12-10-12-15a6 6 0 0 1 12-2 6 6 0 0 1 12 2c0 5-4 9-12 15z" fill="#5a5a5a"/>
<rect x="530" y="132" width="48" height="7" rx="3.5" fill="#9a9a9a"/><rect x="530" y="146" width="32" height="6" rx="3" fill="#bdbdbd"/>
''')

C['ops-legal'] = (False, '''
<g transform="translate(-6 -42)">
<path d="M120 160l70-70 16 16-70 70z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M120 160l-14 30 30-14z" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M190 90l12-12 16 16-12 12z" fill="#3a3a3a" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
</g>
<path d="M60 168c30 0 50-6 70-14" fill="none" stroke="#bdbdbd" stroke-width="2" stroke-linecap="round"/>
<rect x="270" y="20" width="160" height="184" rx="8" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<rect x="290" y="38" width="80" height="10" rx="5" fill="#5a5a5a"/>
<g fill="#bdbdbd"><rect x="290" y="62" width="120" height="6" rx="3"/><rect x="290" y="74" width="110" height="6" rx="3"/><rect x="290" y="86" width="96" height="6" rx="3"/><rect x="290" y="106" width="120" height="6" rx="3"/><rect x="290" y="118" width="104" height="6" rx="3"/><rect x="290" y="130" width="70" height="6" rx="3"/></g>
<path d="M292 172c10-22 20-22 18 0s14-18 22-6 10 8 18-2 12 2 20-4" fill="none" stroke="#3a3a3a" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>
<path d="M290 182h90" stroke="#9a9a9a" stroke-width="2"/>
<circle cx="424" cy="170" r="26" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
<circle cx="424" cy="170" r="18" fill="none" stroke="#ffffff" stroke-width="1.5" stroke-dasharray="3 3"/>
<path d="M415 170l6 6 11-12" fill="none" stroke="#ffffff" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/>
<path d="M520 44v128" stroke="#3a3a3a" stroke-width="3"/>
<path d="M490 178h60" stroke="#3a3a3a" stroke-width="5" stroke-linecap="round"/>
<path d="M470 62h100" stroke="#3a3a3a" stroke-width="3" stroke-linecap="round"/>
<circle cx="520" cy="44" r="6" fill="#3a3a3a"/>
<path d="M480 62l-14 40M480 62l14 40M560 62l-14 40M560 62l14 40" stroke="#9a9a9a" stroke-width="2"/>
<path d="M461 102a19 11 0 0 0 38 0z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M541 102a19 11 0 0 0 38 0z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
''')

C['ops-project'] = (False, '''
<path d="M150 36v116" stroke="#8a8a8a" stroke-width="2" stroke-dasharray="5 5"/>
<rect x="40" y="46" width="90" height="12" rx="6" fill="#5a5a5a"/>
<rect x="80" y="68" width="70" height="12" rx="6" fill="#9a9a9a"/>
<rect x="60" y="90" width="110" height="12" rx="6" fill="#bdbdbd"/>
<rect x="120" y="112" width="60" height="12" rx="6" fill="#e6e6e6" stroke="#3a3a3a" stroke-width="1.5"/>
<path d="M184 128l9 9-9 9-9-9z" fill="#3a3a3a"/>
<rect x="210" y="20" width="400" height="180" rx="12" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<rect x="226" y="30" width="50" height="8" rx="4" fill="#5a5a5a"/><rect x="356" y="30" width="44" height="8" rx="4" fill="#9a9a9a"/><rect x="486" y="30" width="36" height="8" rx="4" fill="#bdbdbd"/>
<g fill="#f2f2f2"><rect x="222" y="46" width="122" height="144" rx="8"/><rect x="352" y="46" width="122" height="144" rx="8"/><rect x="482" y="46" width="118" height="144" rx="8"/></g>
<g fill="#ffffff" stroke="#3a3a3a" stroke-width="1.5">
<rect x="230" y="56" width="106" height="36" rx="6"/><rect x="230" y="100" width="106" height="36" rx="6"/><rect x="230" y="144" width="106" height="36" rx="6"/>
<rect x="360" y="56" width="106" height="36" rx="6"/>
<rect x="490" y="56" width="102" height="36" rx="6"/><rect x="490" y="100" width="102" height="36" rx="6"/>
</g>
<rect x="360" y="100" width="106" height="36" rx="6" fill="none" stroke="#9a9a9a" stroke-width="1.5" stroke-dasharray="5 4"/>
<g transform="rotate(-6 420 130)"><rect x="372" y="112" width="106" height="36" rx="6" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/><rect x="382" y="122" width="56" height="6" rx="3" fill="#5a5a5a"/><rect x="382" y="134" width="36" height="5" rx="2.5" fill="#bdbdbd"/><circle cx="464" cy="130" r="7" fill="#9a9a9a"/></g>
<g fill="#9a9a9a"><rect x="240" y="66" width="56" height="6" rx="3"/><rect x="240" y="110" width="44" height="6" rx="3"/><rect x="240" y="154" width="60" height="6" rx="3"/><rect x="370" y="66" width="50" height="6" rx="3"/><rect x="500" y="66" width="48" height="6" rx="3"/><rect x="500" y="110" width="40" height="6" rx="3"/></g>
<g fill="#bdbdbd"><rect x="240" y="78" width="36" height="5" rx="2.5"/><rect x="240" y="122" width="50" height="5" rx="2.5"/><rect x="240" y="166" width="30" height="5" rx="2.5"/><rect x="370" y="78" width="40" height="5" rx="2.5"/><rect x="500" y="78" width="34" height="5" rx="2.5"/><rect x="500" y="122" width="44" height="5" rx="2.5"/></g>
<g fill="#bdbdbd"><circle cx="322" cy="74" r="7"/><circle cx="322" cy="118" r="7"/><circle cx="322" cy="162" r="7"/><circle cx="452" cy="74" r="7"/></g>
<circle cx="578" cy="74" r="8" fill="#5a5a5a"/><circle cx="578" cy="118" r="8" fill="#5a5a5a"/>
<path d="M574 74l3 3 5-6M574 118l3 3 5-6" fill="none" stroke="#ffffff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
''')

C['ops-support'] = (False, '''
<path d="M118 112a52 52 0 0 1 104 0" fill="none" stroke="#3a3a3a" stroke-width="6" stroke-linecap="round"/>
<rect x="104" y="100" width="26" height="46" rx="11" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
<rect x="210" y="100" width="26" height="46" rx="11" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
<path d="M224 144c0 20-18 28-42 28" fill="none" stroke="#3a3a3a" stroke-width="3" stroke-linecap="round"/>
<rect x="164" y="164" width="20" height="14" rx="7" fill="#3a3a3a"/>
<path d="M290 44a14 14 0 0 1 14-14h152a14 14 0 0 1 14 14v22a14 14 0 0 1-14 14H322l-18 14v-14a14 14 0 0 1-14-14z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<rect x="308" y="44" width="110" height="7" rx="3.5" fill="#9a9a9a"/><rect x="308" y="58" width="76" height="7" rx="3.5" fill="#bdbdbd"/>
<path d="M380 110a14 14 0 0 1 14-14h172a14 14 0 0 1 14 14v22a14 14 0 0 1-14 14h-8v14l-18-14H394a14 14 0 0 1-14-14z" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<rect x="398" y="110" width="124" height="7" rx="3.5" fill="#ffffff"/><rect x="398" y="124" width="84" height="7" rx="3.5" fill="#bdbdbd"/>
<path d="M548 128c-6-4-9-7-9-11a4.5 4.5 0 0 1 9-1.5 4.5 4.5 0 0 1 9 1.5c0 4-3 7-9 11z" fill="#ffffff"/>
<rect x="300" y="160" width="86" height="36" rx="14" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<g fill="#3a3a3a"><circle cx="326" cy="178" r="4"/><circle cx="343" cy="178" r="4" fill="#9a9a9a"/><circle cx="360" cy="178" r="4" fill="#bdbdbd"/></g>
''')

C['ops-logistics'] = (False, '''
<path d="M70 86c40 0 50 50 100 50s50-30 76-30" fill="none" stroke="#8a8a8a" stroke-width="2" stroke-dasharray="6 6"/>
<path d="M70 90c-14-16-22-26-22-38a22 22 0 0 1 44 0c0 12-8 22-22 38z" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<circle cx="70" cy="52" r="7" fill="#ffffff"/>
<path d="M250 176h360" stroke="#bdbdbd" stroke-width="2" stroke-linecap="round"/>
<rect x="250" y="134" width="42" height="40" rx="3" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/>
<rect x="258" y="96" width="36" height="38" rx="3" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<rect x="294" y="140" width="30" height="34" rx="3" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<path d="M271 134v14M276 96v12M309 140v10" stroke="#9a9a9a" stroke-width="3"/>
<rect x="336" y="56" width="164" height="100" rx="6" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<path d="M352 76h132M352 92h100" stroke="#dedede" stroke-width="4" stroke-linecap="round"/>
<path d="M500 80h44l32 36v40h-76z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M512 92h28l20 24h-48z" fill="#ffffff" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<g stroke="#3a3a3a" stroke-width="2"><circle cx="380" cy="160" r="17" fill="#3a3a3a"/><circle cx="540" cy="160" r="17" fill="#3a3a3a"/></g>
<g fill="#ffffff"><circle cx="380" cy="160" r="6"/><circle cx="540" cy="160" r="6"/></g>
<rect x="566" y="128" width="10" height="8" rx="2" fill="#5a5a5a"/>
''')

C['dom-health'] = (False, '''
<path d="M20 112H600" stroke="#dedede" stroke-width="2"/>
<g transform="rotate(-30 95 85)">
<rect x="60" y="70" width="70" height="30" rx="15" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<path d="M95 70h20a15 15 0 0 1 0 30h-20z" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
</g>
<rect x="170" y="40" width="110" height="140" rx="8" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/>
<rect x="200" y="32" width="50" height="16" rx="4" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
<g fill="#ffffff" stroke="#3a3a3a" stroke-width="2"><rect x="186" y="66" width="12" height="12" rx="2"/><rect x="186" y="92" width="12" height="12" rx="2"/><rect x="186" y="118" width="12" height="12" rx="2"/></g>
<path d="M188 72l3 3 5-6M188 98l3 3 5-6" fill="none" stroke="#3a3a3a" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
<g fill="#bdbdbd"><rect x="206" y="69" width="58" height="6" rx="3"/><rect x="206" y="95" width="46" height="6" rx="3"/><rect x="206" y="121" width="52" height="6" rx="3"/><rect x="186" y="148" width="78" height="6" rx="3"/></g>
<path d="M420 166c-52-34-76-60-76-90 0-20 16-36 36-36 16 0 30 10 40 24 10-14 24-24 40-24 20 0 36 16 36 36 0 30-24 56-76 90z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M356 108h22l10-22 14 46 14-56 12 32h40" fill="none" stroke="#3a3a3a" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/>
<path d="M530 38h24v16h16v24h-16v16h-24V78h-16V54h16z" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
''')

C['gen-ideas'] = (False, '''
<g transform="rotate(-6 110 80)"><rect x="70" y="40" width="80" height="80" rx="4" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2"/><path d="M84 62h48M84 76h40M84 90h30" stroke="#9a9a9a" stroke-width="3" stroke-linecap="round"/></g>
<g transform="rotate(5 210 110)"><rect x="172" y="74" width="76" height="76" rx="4" fill="#ffffff" stroke="#3a3a3a" stroke-width="2"/><path d="M186 96h46M186 110h30" stroke="#bdbdbd" stroke-width="3" stroke-linecap="round"/><circle cx="226" cy="132" r="6" fill="#5a5a5a"/></g>
<path d="M266 34l5 13 13 5-13 5-5 13-5-13-13-5 13-5z" fill="#5a5a5a"/>
<path d="M298 150l3 8 8 3-8 3-3 8-3-8-8-3 8-3z" fill="#bdbdbd"/>
<circle cx="56" cy="150" r="5" fill="#9a9a9a"/><circle cx="300" cy="96" r="4" fill="#3a3a3a"/>
<path d="M400 12v14M346 36l10 10M454 36l-10 10M326 92h14M460 92h14" stroke="#9a9a9a" stroke-width="3" stroke-linecap="round"/>
<path d="M400 38a48 48 0 0 0-29 86c6 6 9 13 9 21v5h40v-5c0-8 3-15 9-21a48 48 0 0 0-29-86z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M388 140v-24l6 6 6-12 6 12 6-6v24" fill="none" stroke="#3a3a3a" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
<rect x="380" y="150" width="40" height="9" rx="3" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
<rect x="382" y="162" width="36" height="9" rx="3" fill="#5a5a5a" stroke="#3a3a3a" stroke-width="2"/>
<path d="M390 174h20l-4 8h-12z" fill="#3a3a3a"/>
<path d="M466 124c30 6 40 30 20 50" fill="none" stroke="#8a8a8a" stroke-width="2" stroke-dasharray="5 5"/>
<path d="M584 72L466 118l50 14z" fill="#ffffff" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M584 72l-68 60 20 42z" fill="#f2f2f2" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
<path d="M516 132l4 24 16 18" fill="none" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
''')
