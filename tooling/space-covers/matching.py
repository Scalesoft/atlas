"""Hand-tuned matching data of the built-in space covers: id -> (stems, icons).

The server picks a drawing for a space from its name, description and icon (CoverCatalog.Match in
src/Atlas.Server/Features/SpaceCovers/Domain/CoverCatalog.cs). generate.py writes both lists into catalog.json.

stems  Normalised words (lower case, no diacritics, letters and digits only). A stem longer than three characters matches
       every word it is a prefix of, so one stem covers the Czech forms of a word: "zaloh" matches zalohy, zalohovani and
       zaloha, and not zalozeni. A stem of three characters or fewer matches a whole word only (sql, hr, ai), so "hr" is
       not "hry". When the short form of a word must match too, list its longer forms beside it (kod kodu kody kodov).
       Avoid a prefix that also starts an unrelated common word (disku / diskuze, reklam / reklamace, intern / internet).
       A tie goes to the earlier entry whatever the topic, so a word that only qualifies a topic (interni, oddeleni, audit,
       uzivatel) is listed in no entry that would win it. A word that is half of a phrase is listed in both entries:
       the entry that has both halves scores twice and wins the phrase (bezpecnost + prace is health and safety, zalohove +
       faktury a deposit invoice), the other entries keep the word alone (prace is HR, bezpecnost is IT, zaloh a backup).
icons  Icons of the space appearance catalog that imply the drawing. An icon alone picks the first entry in catalog
       order that lists it, so list an icon where a space with no other hint should land. book-open is the default icon
       and implies nothing; generate.py refuses it.

A name hit outweighs the icon, the icon outweighs a description hit, a tie goes to the earlier entry. After a change run
generate.py and the test CoverCatalogTests (dotnet test tests/Atlas.Server.Tests -c Release --filter
"FullyQualifiedName~CoverCatalog"): it holds the table of realistic space names and checks that every drawing is the
pick for its own name.
"""
MATCH = {
    'it-ai-ml': ('ai ml strojove neuron inteligenc machine', ''),
    'it-analytics': ('analyti report bi dashboard statisti', 'bar-chart-3 layout-dashboard'),
    'it-api': ('api integrac webhook endpoint graphql', 'plug'),
    'it-automation': ('automatiza workflow skript robot', 'workflow'),
    'it-backup': ('zaloh obnov backup restore recovery archivac snapshot', 'refresh-cw'),
    'it-bugs': ('chyby chyba chybov bug bugy bugs issue youtrack jira', ''),
    'it-certificates': ('certifikat klice klicu sifrov tls ssl pki', 'key-round'),
    'it-cicd': ('ci cd cicd build pipeline nasazen deploy', 'rotate-cw'),
    'it-cloud': ('cloud hosting azure aws gcp skalov', 'server'),
    'it-cluster': ('cluster klastr kubernetes k8s orchestra uzly helm', ''),
    'it-code': ('kod kodu kody kodov code vyvoj programov', 'code-xml code'),
    'it-code-review': ('review code diff pull', ''),
    'it-config': ('konfigura nastaven parametr config prostredi', 'settings-2 sliders-horizontal'),
    'it-containers': ('kontejner docker podman registry', ''),
    'it-data-pipeline': ('etl datov transformac dwh', ''),
    'it-database': ('data sql db tabulk postgres mysql mssql', 'database'),
    'it-design': ('ux ui design figma prototyp wirefram rozhran', 'palette'),
    'it-dev-docs': ('dokumentac vyvojar readme sdk navod docs', ''),
    'it-devices': ('zarizen notebook mobily mdm intune telefon', ''),
    'it-email': ('mail email exchange schrank newsletter outlook smtp', ''),
    'it-firewall': ('firewall ddos filtrovan waf', ''),
    'it-frontend': ('frontend vue react css komponent html javascript typescript', ''),
    'it-git': ('git repozitar vetv merge github gitlab commit branch', ''),
    'it-hardware': ('hardware hw pocitace pocitacu pocitacich monitory periferi vybaven pracovist tiskar', ''),
    'it-iam': ('identit pristup opravnen sso entra ldap', 'fingerprint'),
    'it-incident': ('incident vypadk vypadek eskalac postmortem krizov havari', 'shield-alert'),
    'it-iot': ('iot senzor telemetri cidl meren', ''),
    'it-licenses': ('licenc predplat saas subscription', ''),
    'it-llm': ('llm ai asistent chatbot prompt rag claude gpt copilot', 'sparkles'),
    'it-logs': ('log logy logu logov diagnosti', 'history'),
    'it-messaging': ('fronty fronta udalost rabbitmq kafka wolverine messaging', ''),
    'it-migration': ('migrac moderniza legacy upgrade prechod', ''),
    'it-mobile': ('mobiln ios android flutter', ''),
    'it-monitoring': ('monitoring provoz dohled alert sla metrik', 'activity'),
    'it-network': ('sit site siti sitov lan wifi router switch kabel konektivit', 'network'),
    'it-on-call': ('pohotovost oncall rozpis', ''),
    'it-performance': ('vykon optimaliz latenc rychlost zatez performance benchmark', ''),
    'it-release': ('release vydani changelog launch verz', ''),
    'it-scheduling': ('cron planovane uloh job joby scheduler', ''),
    'it-search': ('vyhledav hledan indexac fulltext elasticsearch embedding search', 'search'),
    'it-security': ('bezpecnost zabezpec hesl hesel security kyber pentest', 'shield shield-check lock'),
    'it-storage': ('ulozist disky diskov nas soubor slozk sharepoint onedrive', 'folder'),
    'it-terminal': ('terminal cli powershell bash shell ssh skript prikazov konzol', 'square-code'),
    'it-testing': ('test qa regres', ''),
    'it-tracing': ('observabilit tracing opentelemetry otel profilov trasov trace', ''),
    'it-virtualization': ('virtual vm hyper vmware', ''),
    'it-vpn': ('vpn vzdalen tunel remote', ''),
    'it-web': ('web weby webu webov domen dns url www', 'globe'),
    'ops-benefits': ('benefit stravenk prispev vyhody', ''),
    'ops-brand': ('znack brand loga vizualn', 'palette'),
    'ops-budget': ('rozpoc uspory uspora cashflow', 'pie-chart'),
    'ops-communication': ('komunikac novink intranet oznamen zpravodaj datov schran', 'megaphone'),
    'ops-content': ('obsahov copywrit clank redakc publikac text blog', 'pencil'),
    'ops-crm': ('crm kontakt adresar klient', ''),
    'ops-customer-journey': ('zakazni journey retenc', ''),
    'ops-decisions': ('rozhodnut hlasovan schval adr', 'user-check'),
    'ops-events': ('teambuilding akce akci oslav vecirk vyroci event', ''),
    'ops-feedback': ('zpetn feedback nps anket spokojenost dotaznik', 'smile'),
    'ops-finance': ('financ ucetn naklad dane danov bank', ''),
    'ops-gdpr': ('gdpr udaj soukrom souhlas dpo', ''),
    'ops-hr': ('hr lide lidsk personal zamestnan prace karier', 'users user'),
    'ops-invoicing': ('faktur platb plateb zalohove zalohovych pohledav doklad uhrad', ''),
    'ops-kpi': ('kpi vysledk scorecard okr cile', 'line-chart'),
    'ops-legal': ('pravo pravn smlouv smluv compliance legal', 'scroll-text'),
    'ops-logistics': ('logisti doprav rozvoz tras zasilk preprav', ''),
    'ops-marketing': ('marketing kampan socialn site siti pr seo reklamy reklamn propagac', 'megaphone'),
    'ops-meetings': ('porady porada poradu schuzk meeting zapis', 'calendar'),
    'ops-mgmt-reporting': ('report vedeni board manazer', ''),
    'ops-office': ('kancelar provoz facility udrzb rezervac mistnost recepc', 'building-2'),
    'ops-onboarding': ('onboarding nastup zamestnan zaskolen adaptac', ''),
    'ops-org-chart': ('organizacn struktur organigram odpovednost role', ''),
    'ops-partners': ('partner dodavatel spoluprac outsourc', ''),
    'ops-payroll': ('mzdy mzda mzdov mezd vyplat odmen bonus', ''),
    'ops-pipeline': ('pipeline obchodn prilezitost forecast konverze konverzi leady funnel', ''),
    'ops-policies': ('smernic pravidl predpis priruc postup politik', 'list-ordered'),
    'ops-procurement': ('nakup objednav poptav eshop', ''),
    'ops-product': ('produkt product backlog pozadav priorit', 'box'),
    'ops-project': ('projekt planovan kanban harmonogram ukol sprint scrum agil', 'list-checks'),
    'ops-quality': ('kvalit iso audit neshod zlepsov proces', ''),
    'ops-quotes': ('nabidk poptav cenik kalkulac cenov', ''),
    'ops-recruitment': ('nabor zamestnan kandidat pohovor cv recruit', ''),
    'ops-research': ('vyzkum vyvoj analyz pruzkum rozhovor studie', ''),
    'ops-risk': ('rizik kontinuit bcp pojist hrozb', 'triangle-alert'),
    'ops-roadmap': ('roadmap milnik terminy terminu', ''),
    'ops-safety': ('bozp bezpecnost prace pomuck uraz pozarn', 'octagon-alert'),
    'ops-sales': ('obchod klient prodej sales', ''),
    'ops-strategy': ('strategi vize vizi cile', 'compass'),
    'ops-support': ('podpor helpdesk tiket ticket faq zakazni servis servicedesk support reklamac', 'ticket circle-help'),
    'ops-sustainability': ('udrzitelnost esg ekolog recykl uhlik emis', ''),
    'ops-tenders': ('vyberov tendr tender zakazk', ''),
    'ops-time-tracking': ('vykaz dochazk timesheet', 'clock'),
    'ops-training': ('skolen zamestnan vzdelav kurz learning trenink akademi', ''),
    'ops-travel': ('sluzebn cest letenk ubytovan', 'map-pin'),
    'ops-vacation': ('dovolen absenc volno nemocensk smeny', ''),
    'ops-warehouse': ('sklad zasob inventur expedic', ''),
    'dom-health': ('zdravotn pacient telemedicin klinik nemocnic lekar ordinac', ''),
    'gen-ideas': ('napad sandbox brainstorm pokus poznamk piskovist experiment inovac', 'lightbulb'),
}
