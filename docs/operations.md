# Provoz a vývoj Atlasu

Podrobnosti k profilu, produkční distribuci, přihlášení, hledání, testům, migracím a zálohám. Rychlý start je v [README](../README.md).

## Profil a pracovní plocha

Profil se otevírá v pravé pracovní ploše na `#/profile/account`; další přímo odkazovatelné záložky jsou `security`, `visits`, `appearance` a `api`. Obsahuje účet a kontaktní e-mail, dostupné údaje o přihlášení, zabezpečení, historii návštěv, vzhled a osobní API tokeny. Kontaktní e-mail není přihlašovací identita a neslučuje účty. Údaje o starších přihlášeních mohou chybět.

Přechod do profilu a náhled vzhledu zachovají připojený editor, rozepsaný obsah i undo. Změny profilu mají explicitní uložení, ochranu před opuštěním neuloženého formuláře a před přepsáním souběžné změny. Další akce stránky (export, uspořádání, historie) jsou v nabídce; uložení, publikování, odkaz a oblíbené zůstávají přímo dostupné podle oprávnění a stavu stránky.

Rozbalení navigačního stromu se pamatuje v tomto prohlížeči odděleně pro každý účet. Ukládají se pouze omezené seznamy ID; obsah a přístup se při obnovení načítají znovu ze serveru. Otevření výsledku hledání rozbalí jeho předky a ukáže aktivní stránku ve stromu. Široké tabulky mají vlastní posuvnou oblast, takže neroztahují pracovní plochu.

Editor používá rich JSON, volitelné pluginy příloh a stabilních odkazů na nadpisy a plovoucí osnovu. Upload a soukromé stahování zajišťuje API hostitele. Atlas výslovně používá spolupracující schéma verze 2 a při výpadku spojení další editaci uzamkne; neodeslaný obsah zůstává v aktuálním okně. Obnovení historické revize nebo odvolání oprávnění zneplatní starou generaci editoru. Sdílený editor má tyto hostitelské volby popsané ve svém [README](../src/Atlas.Markdown/README.md).

## Produkční distribuce: jeden .NET proces

```powershell
dotnet publish src/Atlas.Server/Atlas.Server.csproj --configuration Release --output artifacts/publish
```

Výstup `artifacts/publish/` obsahuje server a sestavenou Vue aplikaci v `wwwroot`. Na hostu je potřeba ASP.NET Core Runtime 11 a připojení k PostgreSQL 19 s pgvectorem; Node.js, Vite, Rider ani Aspire nejsou potřeba. Konfiguraci `ConnectionStrings__atlas-db` dodat bezpečně z prostředí nebo správce tajemství. Přihlašovací údaje neukládat do repozitáře.

Z adresáře publikace:

```powershell
dotnet Atlas.Server.dll
```

Veřejný přístup vést přes HTTPS. Při použití reverzní proxy explicitně nastavit důvěryhodné proxy a přeposílané hlavičky; nepovolovat libovolné zdroje. Databázový účet a zálohování připravit pro samostatné produkční prostředí, nikdy nepoužívat vývojový Podman volume jako produkční data.

[Postup zálohy a ověření obnovy](backup-restore.md) zahrnuje databázi, soubory, klíče i odpovídající vydání aplikace.

## Sestavení a ověření

```powershell
dotnet restore Atlas.slnx
dotnet build Atlas.slnx
dotnet test Atlas.slnx -p:SkipFrontendBuild=true
```

NuGet verze jsou připnuté přímo v projektech (lockfily `packages.lock.json` se nepoužívají), frontend v `src/Atlas.Web/pnpm-lock.yaml`. Po schválené aktualizaci závislostí aktualizovat příslušný projekt nebo lockfile; frontendová ověření používají `pnpm install --frozen-lockfile`. Testy vyžadující skutečnou databázi mají vlastní konfiguraci oddělenou od vývojových dat.

Jak a kdy spouštět testy (smoke, celá sada, e2e) popisuje sekce [Testy](#testy) níže a pravidla pro přispěvatele [CONTRIBUTING.md](../CONTRIBUTING.md).

Runtime reference: [Aspire PostgreSQL](https://aspire.dev/integrations/databases/postgres/postgres-host/), [Rider Aspire/Podman](https://www.jetbrains.com/help/rider/NET_Aspire_run_configuration.html), [PostgreSQL vydání](https://www.postgresql.org/docs/release/).
## První instalační průchod a stav implementace

Instalační wizard běží na stejné adrese jako wiki. Databázové připojení se dodává serveru konfigurací `ConnectionStrings__atlas-db`; wizard nikdy nevrací heslo databáze do prohlížeče. První založení wiki je chráněné instalačním tokenem `Setup__BootstrapToken` (alespoň 32 náhodných znaků), který správce předá bezpečným kanálem. První náhodný návštěvník se nesmí stát správcem. Po úspěšném založení další setup odmítne databázově serializovaná operace, nikoliv pouze skrytá obrazovka. Token poté lze odebrat z konfigurace.

Aspire při prvním spuštění vygeneruje parametr `bootstrap-token` a uchová jej v lokálních user secrets AppHostu. **Token pro wizard najdete v Aspire dashboardu → prostředek `atlas-service` → Logs, ve zprávě `Atlas first installation token`.** Server jej také uloží do `bootstrap-token.txt`; průvodce ukazuje přesnou absolutní cestu. Bez dodaného `Setup:BootstrapToken` jej server sám vytvoří a zachová na disku. Vlastní hodnotu lze v Aspire dodat přes `Parameters:bootstrap-token`, v samostatném serveru přes `Setup:BootstrapToken`. Po dokončení instalace se instalační token do logu nevypisuje.

Serverové tokeny jsou pod `Security:DataPath` (výchozí `.data/security` pod content rootem). Pro produkci nastavte trvalý adresář mimo výměnný publish. `administrator-recovery.json` obsahuje oddělený jednorázový token vázaný na existující účet WikiAdmin. V přihlášení zvolte obnovu správce a vložte token ze serveru; nastavíte nové heslo, staré session se zneplatní a nový token nahradí použitý. Soubor platí sedm dní, běžící server jej při expiraci obnovuje. `Recovery:Username` může určit konkrétní existující účet správce; po prvním výběru zůstává vazba na jeho stabilní ID. V režimu OIDC-only musí správce na serveru nejprve zapnout builtin a restartovat.

Instalační token se záměrně vypisuje jednou za start před instalací, i s cestou k souboru; přístup k logům před instalací je proto součástí správcovského přístupu. Obnovovací token se do logu nevypisuje, log při jeho vytvoření/rotaci uvede jen účet, platnost a cestu k souboru. API tokeny, běžná hesla a OIDC code/state se tímto způsobem nelogují.

Backend používá verzované SQL migrace s kontrolou použité verze a transakčním zámkem. Pro požadovaný model „nastavit DB a spustit“ provede host známé migrace při inicializaci databáze; nejde o NHibernate automatické přepisování schématu. Upgrade produkce vyžaduje zálohu a ověřenou migraci, novější schéma starší server odmítne. Podporovaný provoz má jeden aktivní aplikační host. Sdílená databáze a klíče samy nezajišťují distribuovanou koordinaci SignalR V1 ani importu.

Read-only modul YouTrack přijímá serverovou konfiguraci `YouTrack:Enabled`, `YouTrack:BaseUrl` (HTTPS), `YouTrack:Token` a `YouTrack:SpaceProjects` (mapa UUID prostoru na povolená stabilní ID YouTrack projektů). Host nejprve ověří přístup do prostoru; klient navíc filtruje skutečné projektové ID každého výsledku. Neukonfigurovaná integrace je nedostupná, nikoliv fiktivně funkční. Nikdy neposílá token do Vue nebo dokumentu.


## Nastavení modulů bez úprav konfiguračních souborů

Po vytvoření prvního správce pokračuje instalační průvodce třemi volitelnými kroky **Firemní přihlášení**, **YouTrack** a **Import Wiki.js**. Krok můžete odložit. Po reloadu se rozpracované dokončení instalace obnoví. Na již existující wiki otevřete **Správa wiki**; stejné formuláře jsou v **Lidé → Přihlašování** (Firemní přihlášení), **AI a integrace** (YouTrack) a **Provoz → Import z Wiki.js**.

1. **OIDC / Hub:** zadejte veřejnou HTTPS adresu wiki, přesný issuer, Client ID a secret. Formulář vypíše callback URL pro registraci v Hubu. Uložte a ověřte discovery. Restartujte server, potom v přihlášení použijte tlačítko poskytovatele. Test discovery nenahrazuje skutečný login. Existující účet se propojuje v profilu po ověření identity; e-mail se k automatickému slučování nepoužívá.
2. **YouTrack:** zadejte HTTPS adresu a permanent token servisního účtu, uložte a otestujte spojení. Povolte stabilní ID projektů pro každý prostor; bez povoleného projektu se nic nevyhledá. Konfigurace spojení se uplatní bez restartu.
3. **Import Wiki.js:** nahrajte ZIP se stránkami a přílohami (nejvýše 64 MiB), určete stabilní identifikátor zdroje a nový importní prostor. Stránky psané v Markdownu i ve vizuálním editoru Wiki.js (`.md` i `.html`) se importují stejně a mají stejnou identitu podle cesty bez přípony; má-li jedna stránka obě podoby, přednost dostane Markdown. Formuláře, skripty a rozbalovací widgety z HTML nemají v Markdownu obdobu: vypíšou se u dané stránky do varování a zůstávají jen v původním archivu. Stránka, ze které by po převodu nezbyl žádný obsah nebo která by přesáhla 1 MB, se nahlásí jako nepodporovaná místo aby shodila celý import. Nejdříve zkontrolujte náhled, konflikty a varování, potom výslovně potvrďte import. Stránky vzniknou jako soukromé koncepty bez automatických grantů. Původní archiv a report lze stáhnout; stejný obsah se při opakování neduplikuje. Zdrojové jazyky/formáty a oprávnění firmy před přechodem ověřte na reprezentativním vzorku.

Secret/token se do formuláře nevrací. Prázdné pole zachová uloženou hodnotu; samostatná volba ji odstraní. Při změně protistrany zadejte nové tajemství. Změna uložená jiným správcem se nepřepíše bez opětovného načtení a potvrzení.

Vývojové testy používají izolované služby a databáze.

## Přihlášení a připojení firemního OIDC

Builtin a obecný OpenIddict klient používají stejný lokální účet a HttpOnly session. Poskytovatelé se registrují při startu, první cílový provider je JetBrains Hub. Bez konfigurace OIDC běží pouze lokální přihlášení. Výpadek IdP neotevře anonymní přístup. Reset či aktivaci lokálního účtu zahajuje WikiAdmin; krátkodobý jednorázový token předá bezpečným kanálem, automatický e-mail tato etapa neposílá.

| Konfigurace | Význam / výchozí chování |
| --- | --- |
| `Authentication:Builtin:Enabled` | `true`; po vypnutí není lokální login dostupný |
| `Authentication:OpenIdConnect:PublicOrigin` | Veřejná HTTPS origin bez cesty/query; povinná, pokud existují providers |
| `Authentication:OpenIdConnect:Providers` | Pole pojmenovaných OIDC registrací; výchozí prázdné |
| `Providers:{n}:Id`, `DisplayName` | Stabilní malé URL-safe ID, název zobrazený v loginu/profilu |
| `Providers:{n}:Issuer`, `ClientId`, `ClientSecret` | Přesný HTTPS issuer z discovery, ID klienta a serverové tajemství |
| `Providers:{n}:Scopes` | Výchozí `openid`, `profile`; `openid` je vždy vyžadovaný, `offline_access` není povolený |
| `Providers:{n}:AllowSignup` | Výchozí `false`; `true` dovolí nový federovaný účet, vždy bez práv a bez builtin hesla |
| `Providers:{n}:EnableProviderLogout` | Pouze `false`; globální odhlášení není implementované |
| `Authentication:OpenIdConnect:SigningCertificatePath` | Volitelná explicitní cesta k internímu signing PFX |
| `Authentication:OpenIdConnect:EncryptionCertificatePath` | Volitelná explicitní cesta k internímu encryption PFX |
| `Authentication:OpenIdConnect:CertificateDirectory` | Bez explicitní dvojice PFX se privátní interní certifikáty spravují zde; výchozí `.data/oidc-certificates` |
| `Authentication:OpenIdConnect:CertificatePassword` | Případné heslo PFX ze správce tajemství, nikoliv repozitáře |
| `DataProtection:KeysPath` | Trvalý sdílený key ring chrání session i OIDC state; přístup omezit na aplikační účet |

Serverová konfigurace poskytovatelů je fallback do prvního uložení v administraci. Poté se používá uložená DB revize, změna platí po restartu. Builtin enable a volitelné explicitní certifikáty zůstávají serverovou konfigurací.

Příklad nesekretní části fallback konfigurace (adresy jsou zástupné). V konkrétním Hubu použít přesný `issuer` z jeho discovery; neodvozovat ho pouhým zkrácením URL portálu.

```json
{
  "Authentication": {
    "Builtin": { "Enabled": true },
    "OpenIdConnect": {
      "PublicOrigin": "https://wiki.example.invalid",
      "SigningCertificatePath": "/run/secrets/oidc-signing.pfx",
      "EncryptionCertificatePath": "/run/secrets/oidc-encryption.pfx",
      "Providers": [
        {
          "Id": "hub",
          "DisplayName": "Firemní účet",
          "Issuer": "https://hub.example.invalid/",
          "ClientId": "atlas",
          "Scopes": ["openid", "profile"],
          "AllowSignup": true
        }
      ]
    }
  }
}
```

`Authentication__OpenIdConnect__Providers__0__ClientSecret` dodat samostatně ze správce tajemství. Do IdP registrovat callback **`https://wiki.example.invalid/signin-oidc/hub`**. Pro každý provider je callback odlišný; implicit flow ani password grant se nepoužívá. V development prostředí se používají OpenIddict development certifikáty. V produkci PFX a Data Protection klíče uchovat při restartech, chránit přístupem aplikačního účtu a zahrnout do zabezpečené obnovy.

Pro přechod builtin → OIDC: přihlásit správce lokálně, v profilu ověřit heslo a propojit firemní účet, vyzkoušet nové přihlášení; až poté případně vypnout builtin a restartovat. OIDC-only režim vyžaduje aktivního WikiAdmin s propojeným nakonfigurovaným providerem, jinak readiness vrací bezpečnou chybu s postupem nápravy. Pro připojení dalšího IdP u OIDC-only uživatele je potřeba nové přihlášení původním providerem nejvýše před pěti minutami. Shodný e-mail nebo zobrazované jméno účty neslučuje; již obsazená externí identita se nepřesouvá. Odebrání poslední metody není vystavené API.

OpenIddict 7.6.1 ověřuje discovery, podpis, issuer, audience, nonce a state, klient vynucuje S256 PKCE. Desetiminutové state tokeny mají NHibernate store s transakčním optimistic concurrency a pravidelným úklidem; není vypnutá ochrana proti replay. Provider access/refresh tokeny se nepersistují a neputují do Vue. Logout ukončí pouze místní Atlas session; existující IdP session může umožnit další login bez nového zadání hesla.

Protokolové testy se skutečnou PostgreSQL ověřují: celý OpenIddict code flow proti in-process IdP discovery/JWKS/token transportu, ověření client secret a PKCE, odmítnutí špatného podpisu/issueru/audience/nonce, chybějící correlation cookie, změněného a opakovaného state, souběžné redemption, propojení s důkazem obou identit a žádné automatické převzetí rolí. Jde o protokolové testy; skutečný IdP ověřte při jeho připojení.

Reference: [OpenIddict client sample](https://github.com/openiddict/openiddict-samples/tree/dev/samples/Velusia/Velusia.Client), [Data Protection integrace](https://documentation.openiddict.com/integrations/aspnet-core-data-protection), [Hub OpenID Connect](https://www.jetbrains.com/help/hub/openid-connect.html).

## Hledání a měřený základ výkonu

Omnisearch hledá výhradně v publikovaných revizích. PostgreSQL udržuje `SearchDocument` transakčním triggerem při publikaci nebo přesunu stránky; samostatný vyhledávací proces není potřeba. GIN indexy kombinují fulltext s prefixy a trigramovou podobnost názvu i těla. České dotazy fungují bez diakritiky, běžné překlepy mají přibližné výsledky. Přesný název a jeho začátek mají přednost. Jde o konfiguraci `simple` s normalizací, nikoliv kompletní českou morfologii či sémantické hledání.

Viditelnost se ověřuje před vrácením výsledků, úryvků, návrhů prostorů a `hasMore`. Odebrání přístupu nečeká na reindexaci. API podporuje filtr prostoru, stránkování po 20 výsledcích (limit 1–50, offset bez horní meze), dotaz do 200 znaků a SQL timeout dvě sekundy. Minimum jsou dva normalizované znaky; technické aliasy zachovávají rozdíl C#/C++. Uvozovky znamenají frázi; odkazy na sekce používají pouze uložené kotvy publikovaných nadpisů. Migrace 021 přidává aliasy z nejvýše 32 posledních publikací a 16 migrovaných cest na stránku. Cesta se zpřístupní teprve po skutečné publikaci její importované revize; soukromé názvy a cesty konceptů se neindexují. Výsledek může obsahovat `matchedAlias`, který UI vysvětlí pod současným názvem. Prohlížeč ukládá stav hledání do URL, krátce odkládá dotazy a ruší předchozí požadavek. Výsledky zůstávají textem, nikoliv důvěryhodným HTML.

Lokální regresní měření 2026-09-07 se schématem do 023 včetně aliasů 021 na PostgreSQL 18.6 v Podmanu: 10 000 publikovaných dokumentů / 50 000 revizí, 10 000 dřívějších publikovaných názvů a 1 000 migrovaných cest, syntetická česká dokumentace, 14 zahřátých vzorků každého dotazu. Benchmark vyžaduje neprázdný výsledek každého měřeného dotazu. Po hromadném přepočtu indexu se měřilo bez předchozího VACUUM. Časy zahrnují SQL a přečtení výsledků, nikoli HTTP nebo vykreslení Vue.

| Dotaz | p50 | p95 |
| --- | ---: | ---: |
| `pristupy` | 16,48 ms | 17,12 ms |
| `autentikace` (překlep) | 109,22 ms | 120,40 ms |
| `nasazeni kube` | 22,20 ms | 22,60 ms |
| `monitoring serveru 500` | 28,52 ms | 29,19 ms |
| `puvodni prirucka 500` (starý název, 11 výsledků) | 3,51 ms | 3,79 ms |
| `legacy route 500` (migrovaná cesta, 1 výsledek) | 2,07 ms | 2,69 ms |

Vyhledávací projekce se čte přes GIN; historie revizí se neprohledává. Migrace 020 vypíná GIN pending lists (`fastupdate=off`), které po hromadném zápisu způsobily regresi nad jednu sekundu. Všech šest EXPLAIN používá aliasový GIN index a žádný nečte PageRevision.

Dvacet nezávislých asynchronních čtenářů provádí po 24 voláních celé SearchAsync služby bez uživatelských pauz, střídá přesný dotaz a překlep a sdílí jeden Reader účet/prostor. Měření zahrnuje načtení aktuálního aktéra, ACL a autorizační zámky, databázový pool, návrhy prostorů, sekce a úryvky. Samostatný worker současně provede 40 dávek po deseti obnovách projekce s 10ms pauzou mezi dávkami; neběží nepřetržitě po celou dobu všech čtenářů. Ze 480 úspěšných neprázdných odpovědí vychází p95 **180,01 ms**, p95 zapisující dávky **21,16 ms**. Tím je splněn místní syntetický backendový cíl 300 ms při dvaceti souběžných volajících. Test nezahrnuje HTTP, přihlášení dvaceti různých účtů, prohlížeč ani firemní relevanci; není to produkční SLA.

Reprodukovatelný nástroj je v `tests/Atlas.Search.Benchmarks/`; hodnoty a EXPLAIN jsou v [baseline-2026-09-07-20-readers.json](../tests/Atlas.Search.Benchmarks/baseline-2026-09-07-20-readers.json). Starší baseline zůstávají pro srovnání. `ATLAS_TEST_DATABASE` musí ukazovat na oddělenou testovací databázi; nástroj vytváří a odstraňuje pouze vlastní náhodné schéma. Výstup lze určit přes `ATLAS_BENCHMARK_OUTPUT`; pro tento dataset nastavit `ATLAS_BENCHMARK_ALIASES=1` a `ATLAS_BENCHMARK_READERS=20` (výchozí počet čtenářů je 2). Místní měření používalo IPv4 spojení na Podman/PostgreSQL 18.6, pooling zapnutý, ANALYZE a žádné předchozí VACUUM.

```powershell
dotnet run --project tests/Atlas.Search.Benchmarks -c Release -p:SkipFrontendBuild=true
```

Stejná proměnná `ATLAS_TEST_DATABASE` zapíná skutečné PostgreSQL testy hledání. Bez ní jsou databázové testy explicitně přeskočené, nikoliv považované za úspěšně ověřenou integraci.

## HTTPS za reverzní proxy

Důvěryhodné proxy se od WP4 V4 spravují v Security centru (Sítě a proxy): pojmenované adresy nebo CIDR a podporovaný počet hopů, uložené v databázi. Explicitní pole `Http:TrustedProxies` (v prostředí například `Http__TrustedProxies__0=10.0.0.10`) se při prvním startu na databázi jednou importuje jako přesné adresy s jedním hopem; pozdější změna prostředí se už neaplikuje, jen se v GUI ukáže jako rozdíl. Než databáze po startu odpoví, host důvěřuje seznamu z prostředí. Host přijímá `X-Forwarded-For` a `X-Forwarded-Proto` pouze od důvěryhodných proxy; prázdný seznam nedůvěřuje žádné. Wildcardy, CIDR rozsahy a DNS jména jsou v `Http:TrustedProxies` odmítnuté při startu. `X-Forwarded-Host` není povolen. Obnova po chybné konfiguraci proxy: nastavit `Http__RecoverTrustedProxies=true` se správným `Http:TrustedProxies` a restartovat; databázový seznam se nahradí (auditovaně, jeden hop) a proxy zůstanou v GUI jen ke čtení, dokud se nastavení neodebere. Obnova nevypíná autorizaci, opakované přihlášení ani audit; alternativou je přímé připojení na Kestrel. Bez proxy nastavit HTTPS přímo na Kestrelu a dodat certifikát bezpečnou konfigurací hostu.

**HTTP/2 na každé proxy.** Kestrel v kontejneru mluví HTTP/1.1; HTTP/2 (případně HTTP/3) musí nabídnout proxy (nginx `listen 443 ssl; http2 on;`, Caddy a Traefik ho mají ve výchozím stavu, u Apache `Protocols h2 http/1.1`). Otevření stránky stahuje přes 160 drobných souborů; měřeno při 40 ms RTT je čas do obsahu na HTTP/1.1 (6 spojení) zhruba dvojnásobný oproti HTTP/2 (1 573–1 919 ms proti 722–951 ms). WebSocket spolupráce a SignalR jedou přes proxy dál, proxy musí propustit `Upgrade`. Ověření: `curl -sI --http2 -o /dev/null -w "%{http_version}" https://<adresa>/` má vypsat `2`; kontrola ALPN: `openssl s_client -alpn h2 -connect <adresa>:443 </dev/null 2>/dev/null | grep -i "ALPN protocol"` má vypsat `h2`.

HTML odpovědi SPA včetně veřejných stránek `/s/*` posílají vynucenou `Content-Security-Policy`; `Http:ContentSecurityPolicy:Enforce=false` (`Http__ContentSecurityPolicy__Enforce=false`) z ní udělá jen `Content-Security-Policy-Report-Only`, výslovnou volbu provozovatele. Vyjmuté jsou `/scalar/*` (Scalar.AspNetCore používá inline skript) a odpovědi s vlastní politikou (kontrola OIDC mappingu, přílohy, exporty). Politika je pevná: `connect-src 'self'` pokrývá i WebSocket SignalR na stejném hostu, `font-src 'self' data:` font ikon Monaco vložený jako `data:` URL. Serverový sběr hlášení (`report-uri`) zatím není; porušení je vidět v konzoli prohlížeče. `img-src` je v serveru napevno včetně `https:`, protože editor dovoluje obrázky z externích adres; konfigurací se nemění. Externí obrázky může omezit jen editor (`resourcePolicy.imageSources.allowedOrigins`). Na veřejných stránkách `/s/*` proto cizí hostitel obrázku vidí IP adresu a prohlížeč čtenáře; adresu sdíleného odkazu díky `Referrer-Policy: no-referrer` nedostane.

Vynucená politika je výchozí (SEC-I-2): je jedinou obranou proti skriptu podstrčenému přes cizí renderer obsahu od pisatele, hlavně Scalar v bloku OpenAPI. Ten běží v `srcdoc` iframu se `sandbox="allow-scripts allow-same-origin"`, protože ho editor plní přímo (konfigurace z realmu rámu, vlastní `fetch`); sandbox ho tedy od původu Atlasu neodděluje, rám ale politiku rodiče dědí. Úplná izolace (rám bez `allow-same-origin` a most přes `postMessage`) je samostatná změna. `tests/browser/csp.spec.ts` ověřuje, že ani vynucená politika aplikaci nerozbije: shell aplikace, stránka s blokem OpenAPI (záložka API se Scalarem, YAML v Monacu s workery) a diagramem Mermaid a veřejná stránka `/s/*` nad publikovaným serverem bez jediného hlášení porušení, v Chromiu, Firefoxu i WebKitu. Spuštění popisuje [README browserových testů](../tests/browser/README.md#content-security-policy--cspspects).

Vite soubory pod `/assets/` s fingerprintem v názvu mají roční immutable cache; JavaScript/CSS/SVG v této větvi využívají Brotli nebo gzip. HTML včetně fallbacku SPA má `no-store`. API odpovědi nejsou komprimované a nesmějí se ukládat do cache. Data Protection klíče uchovat v trvalém adresáři přístupném pouze účtu serveru (`DataProtection__KeysPath`; výchozí `.data/keys` pod content rootem). Adresář musí přežít výměnu publikované aplikace.


## Integrační a prohlížečové testy



### Testy

Backendové testy potřebují jednu jednorázovou PostgreSQL 19 s rozšířením pgvector v `ATLAS_TEST_CONNECTION` (`ATLAS_TEST_DATABASE` pro testy hledání a OIDC ji bez nastavení převezme). Nejrychlejší je Podman na tmpfs:

```powershell
podman run -d --name atlas-test-pg -p 127.0.0.1:55450:5432 -e POSTGRES_PASSWORD=atlas -e POSTGRES_DB=atlas_test --tmpfs /var/lib/postgresql:rw,size=8g docker.io/pgvector/pgvector:0.8.7-pg19-trixie@sha256:ace863afa68a13560603dc7366fad0adb4f5ddd0a21c8f06418195f32e03fa08 -c max_connections=500 -c fsync=off -c full_page_writes=off -c shared_buffers=512MB
$env:ATLAS_TEST_CONNECTION = 'Host=127.0.0.1;Port=55450;Username=postgres;Password=atlas;Database=atlas_test'
```

- **Jak testy dostanou databázi.** Migrace proběhnou jednou za běh do šablonové databáze. Každý `AtlasTestHost`, `AtlasHttpSite` i `DisposableSchema` pak dostane vlastní databázi, klon šablony přes `CREATE DATABASE … TEMPLATE`, se schématem přejmenovaným na náhodné jméno fixture. Po testu se databáze zahodí (`tests/Atlas.Server.Tests/Platform/TestDatabases.cs`). Testovací role proto potřebuje právo CREATEDB.
- **Proč vlastní databáze.** Oddělené databáze drží odděleně i advisory zámky. Dřív všechny migrace čekaly na jeden databázový zámek, takže běžely po jedné. Spojení do těchto databází jsou poolovaná jako v produkci; pool zaniká s databází.
- **Řazení textu je všude stejné.** Každá testovací databáze vzniká s kolací builtin `C.UTF-8` (`CREATE DATABASE … LOCALE_PROVIDER builtin`, `TestDatabases.cs`) jako produkční databáze z `docker-compose.yml`, ať má server jakékoli locale. Test závislý na pořadí proto platí lokálně stejně jako v CI.
- **pgvector je povinný.** Migrace 2026100520 bez něj skončí chybou a fixtures server bez pgvectoru odmítnou hned na začátku. Starší kontejner `atlas-test-pg` (obraz `postgres:18.6` nebo PostgreSQL 18) smažte (`podman rm -f atlas-test-pg`) a spusťte znovu příkazem výše. Nativní záložní PostgreSQL bez Podmanu pgvector nemá.
- **`synchronous_commit` nechte na výchozí hodnotě.** Jeden test ji ověřuje.
- **Nativní PostgreSQL na Windows fixtures odmítnou** jasnou chybou. Nové spojení tam trvá asi 1 s a `CREATE DATABASE` 5–60 s, takže paralelní běh padal na stovkách timeoutů. (Kdo s ní přesto experimentuje, ať nezapíná `wal_level=minimal`: vytvoření každé tabulky pak čeká stovky milisekund.)
- **Localhost.** V connection stringu používejte `Host=127.0.0.1`: `localhost` měl při každém novém spojení dvousekundový IPv6 fallback.

| Kdy | Příkaz | Doba (64 vláken, Podman) |
|---|---|---|
| Při vývoji, podle potřeby | `pwsh tests/changed.ps1` (jen to, čeho se změna dotýká), `pwsh tests/smoke.ps1` (serverové testy s `Category=Smoke`, vue-tsc a vybrané vitest soubory paralelně) | sekundy až 40 s |
| Na každém pull requestu a na `develop` (CI, automaticky) | celá sada: release nástroje, editor a web s vue-tsc, server včetně testů migrací po dávkách, Markdown Sync, Yjs a YouTrack ([ci.yml](../.github/workflows/ci.yml), [server-tests.sh](../tooling/ci/server-tests.sh)) | desítky minut |
| Vývojové nástroje pro větší změny (nejsou součástí CI ani vydání) | `pwsh tests/full.ps1` (celá sada a prohlížečové testy v Podmanu), `pwsh tests/browser/e2e-podman.ps1`, [test upgradu dat](#test-upgradu-dat-vývojový-nástroj) | minuty |

xUnit běží s `maxParallelThreads: 0` (počet procesorů) a se server GC. S výchozím workstation GC jeden sběrač serializoval desítky hostů. Wolverine v testech generuje kód handlerů za běhu; `SharedHandlerCompiler` zkompiluje každý vygenerovaný zdroj jednou za testovací proces, místo aby Roslyn znovu kompiloval každý handler v každém hostu. Stejná sada tak trvá 38 s místo 1,6 min a proces testů potřebuje asi 8 GB místo 44 GB. Vitest používá polovinu CPU; změřené optimum je 16–32 workerů. Bez `ATLAS_TEST_CONNECTION` se databázové testy explicitně přeskočí a `tests/smoke.ps1` skončí chybou.

`tests/browser` obsahuje Playwright přejímku s opravdovým serverem i editorem. [Postup spuštění](../tests/browser/README.md) odděluje první instalaci, následné pracovní scénáře a restart na druhé prázdné databázi. Používá vyhrazené adresáře klíčů a souborů, důvěryhodné HTTPS a jednorázová tajemství v prostředí. Spuštění všech souborů abecedně nad prázdnou databází nenahrazuje tento postup.

## Podman v Rideru na Windows

Ověřený běh Aspire 13.5.3 / DCP 0.25.13 na Windows používá standardní stroj `podman-machine-default`. Pokud ještě neexistuje, jednorázově v terminálu Rideru spustit `podman machine init`. Potom `podman machine start podman-machine-default` a případně `podman system connection default podman-machine-default`. `podman info` musí fungovat právě z terminálu Rideru; samotná běžící WSL VM bez zaregistrovaného spojení nestačí.

Při ověřování pojmenovaného nestandardního stroje DCP selhávalo při kopírování certifikátů s chybou „podman-machine-default: VM does not exist“, přestože `podman info` i běžné příkazy fungovaly. Standardní stroj tuto chybu odstraňuje; konfigurace projektu nemění systémový PATH ani nevypíná TLS. Existující jiné VM a jejich data není potřeba mazat. Nespouštět dvě konfliktní Podman VM současně; před přepnutím ověřit jejich používané služby.

Pro Node.js 24 při HTTPS browserových testech na Windows nastavit v testovacím procesu `$env:NODE_USE_SYSTEM_CA='1'`, aby Node používal důvěryhodné certifikáty systému stejně jako prohlížeč. Vývojový certifikát se nastavuje přes `dotnet dev-certs https --trust`; validace TLS se nevypíná.


## Databázové migrace a create skript

Schéma vytváří a aktualizuje **FluentMigrator 8.0.1** uvnitř stejného serveru. NHibernate zůstává ORM a neprovádí SchemaUpdate. Každá známá migrace má typovanou verzi a používá verzované embedded SQL. Zápis celého balíku i migrační evidence běží v jediné transakci pod PostgreSQL advisory lockem. Existující `SchemaVersion` se převezme, dřívější záznamy a data se nepřepisují. Neznámá verze v databázi start odmítne.

Připojená PostgreSQL databáze musí existovat; lokálně ji připravuje Aspire v Podmanu. Automatický start vytváří aplikační tabulky a potřebná rozšíření. Pro kontrolu či ruční založení prázdného schématu umí tentýž publikovaný runtime vygenerovat offline SQL:

```powershell
dotnet Atlas.Server.dll --database-script C:/deploy/atlas-create.sql
```

Export nevyžaduje databázové připojení, nespouští web ani inicializaci tokenů a nepřepíše existující výstupní soubor. Spuštění v PostgreSQL nad prázdným schématem vytvoří také migrační evidenci, kterou aplikace následně převezme. **Pro existující wiki použijte verzované migrace při startu, nikoliv create skript.** Aktuální atlas-create.sql obsahuje migrace 001–024 a vznikl ze stejného finálního Release serveru; skutečné provedení create skriptu a následné převzetí startupem ověřuje migrační sada. Downgrade není automatický; provozní rollback používá ověřenou zálohu.

## Záloha, upgrade a obnova

Záloha musí tvořit konzistentní celek PostgreSQL a trvalých souborů. Nejjednodušší podporovaný postup je zastavit jediný aplikační host, nechat skončit zápisy a pořídit databázový dump i soubory před dalším spuštěním. Samostatný `pg_dump` poskytuje databázový snapshot, ale nesynchronizuje s ním kopii souborového systému. Při zálohování za provozu musí správce zajistit společnou hranici a uchovat všechny bloby referencované snapshotem; aplikace zatím nemá distribuovaný zálohovací protokol.

| Trvalá součást | Výchozí cesta pod content rootem / konfigurace |
| --- | --- |
| Data Protection klíče | `.data/keys` / `DataProtection:KeysPath` |
| Soukromé OIDC signing/encryption certifikáty | `.data/oidc-certificates` / `Authentication:OpenIdConnect:CertificateDirectory`; také případné explicitní PFX mimo tento adresář |
| Instalační a obnovovací soubory | `.data/security` / `Security:DataPath` |
| Neměnné soubory příloh | `.data/attachments` / `Attachments:DataPath` |
| Uchované zdrojové importní ZIPy | `.data/imports` / `ContentImport:DataPath` |
| Soubory KeePass umístění „Na serveru wiki“ | `.data/vaults` / `Vaults:DataPath` |

Docker image ([Dockerfile](../Dockerfile)) přesměruje všechny tyto cesty pod `/data` (`/data/keys`, `/data/oidc`, `/data/security`, `/data/attachments`, `/data/import`, `/data/vaults`, exporty `/data/export`, mezipaměť zálohy do Gitu `/data/git-backup`), který Compose ukládá do svazku `atlas-data`. Novou trvalou cestu je nutné doplnit také tam, jinak zůstane v `/app/.data` uvnitř kontejneru a nové nasazení ji smaže.

**Databázové role.** Aplikace pracuje jako role `atlas_app` (`ConnectionStrings:atlas-db`), ne jako superuživatel. Migrace, obnova instalace a statistiky běží jako vlastník schématu (`ConnectionStrings:atlas-db-owner`, v Compose `wikiuser`). Vlastník při každém startu po migracích roli `atlas_app` založí nebo upraví (přihlášení bez SUPERUSER, CREATEDB a CREATEROLE, heslo z jejího connection stringu) a dá jí řádky všech tabulek schématu. Na auditních záznamech (`RuntimeDatabaseRole.AppendOnly`) jí nedá UPDATE, DELETE ani TRUNCATE a na historii přístupů jen UPDATE sloupce `ActiveTo`. Hesla jsou v `.env` vedle `docker-compose.yml` (`ATLAS_DB_OWNER_PASSWORD`, `ATLAS_DB_APP_PASSWORD`), ne v Compose. Nová instalace i existující databáze nepotřebují ruční krok: u existujícího svazku stačí do `ATLAS_DB_OWNER_PASSWORD` zapsat stávající heslo `wikiuser`, změnit ho jde přes `ALTER ROLE wikiuser PASSWORD …` a úpravu `.env`. Roli `atlas_app` připraví vlastník hned na začátku startu, dřív než se jako ona cokoli přihlásí; nové heslo `atlas_app` stačí zapsat do `.env` (start ho nastaví, posílá ho jen tehdy, když se role s ním nepřihlásí). Bez `atlas-db-owner` (vývoj, testy) běží všechno pod jednou rolí jako dřív. Aplikace (i Chromium PDF exportu) běží jako neprivilegovaný uživatel `app` (UID 1654): start kontejneru jako root jen předá `/data` tomuto uživateli (co mu ještě nepatří, např. soubory starší verze zapsané rootem) a hned se přepne na `app`; nasazení, které kontejner spouští rovnou jako `1654:1654`, musí svazek předat samo (`chown -R 1654:1654`).

Zahrnout také konfiguraci, bezpečný přístup k jejím tajemstvím a přesnou verzi publikovaných binárek/webu. Výchozí `.data` nestačí, pokud konfigurace odkazuje mimo něj. Dočasné obsahové exporty (`ContentExport:DataPath`, výchozí `.data/exports-tmp`) nejsou zdrojová data. Totéž platí pro lokální kopii [zálohy do Gitu](git-backup.md) (`GitBackup:DataPath`, výchozí `.data/git-backup`): je to mezipaměť, kterou Atlas znovu naklonuje. ZIP článku/prostoru není záloha wiki: neobsahuje účty, konfiguraci, historii celé databáze ani neuložený Yjs stav. DB dump musí zahrnout také `PageCollaboration` a potvrzený `CollaborationUpdate` log.

Zálohu chránit stejně jako produkční tajemství a uložit manifest s SHA-256 souborů. Obnovu nejprve provést do izolované databáze a oddělených datových adresářů se správnou verzí aplikace; ověřit migrační evidenci, hashe příloh, čtení publikace i konceptu a zachování rozepsané spolupráce. Klíče a certifikáty obnovit před startem, jinak mohou být uložená tajemství nečitelná. Obnova starého snapshotu může obnovit tehdejší platnost session či tokenů; jejich revokaci/rotaci před otevřením provozu musí vyřešit správce. Automatický downgrade není podporovaný: rollback znamená návrat celého ověřeného datového i aplikačního celku.


### Upgrade na PostgreSQL 19

Atlas 5.1 nasazuje PostgreSQL 19 v oficiálním image `pgvector/pgvector` (Debian), připnutém digestem v `docker-compose.yml`; vlastní image `atlas-postgres` končí s 5.0. Text dál řadí podle kódových bodů jako dřívější Alpine image (viz [Kolace databáze](#kolace-databáze)). Data PostgreSQL 18 nový image neotevře: kontejner `wiki-db` skončí chybou „… holds PostgreSQL 18 data … upgrade the volume first“ (pojistka v `entrypoint` v `docker-compose.yml`), Atlas nenastartuje a data zůstanou beze změny. Jednou je přenese `deploy/postgres/upgrade.sh` (bash a Docker nebo Podman na hostiteli svazku):

Postup pro instalaci Atlasu 5.0 (`docker-compose.yml` 5.0.0 připíná `ghcr.io/scalesoft/atlas-postgres:18.6-pgvector0.8.7-r2`):

1. **Záloha** podle předchozí části (databáze i `/data`), ověřená.
2. **Zjistit starý image a svazek, pak zastavit vše, co svazek používá.** Image, na kterém databáze běží: `docker inspect --format '{{.Config.Image}}' $(docker compose ps -q wiki-db)` (u 5.0.0 výše; jiný image `atlas-postgres` s PostgreSQL 18 z dřívější instalace poslouží stejně, skript ho jen použije ke čtení dat). Svazek: `docker volume ls --filter name=wiki-db-data` (Compose mu dává předponu projektu, např. `atlas_wiki-db-data`). Pak `docker compose stop`; skript jinak odmítne běžet. Mezi tímto krokem a startem 5.1 nespouštějte databázi se starým image: co by se zapsalo do 18, v 19 by chybělo.
3. **Upgrade svazku** (`deploy/postgres/upgrade.sh` z vydání 5.1, např. `curl -fsSLO https://raw.githubusercontent.com/Scalesoft/atlas/v5.1.0/deploy/postgres/upgrade.sh`):
   ```sh
   bash deploy/postgres/upgrade.sh atlas_wiki-db-data ghcr.io/scalesoft/atlas-postgres:18.6-pgvector0.8.7-r2 \
     docker.io/pgvector/pgvector:0.8.7-pg19-trixie@sha256:ace863afa68a13560603dc7366fad0adb4f5ddd0a21c8f06418195f32e03fa08
   ```
   Nový image je ten, který připíná `docker-compose.yml` verze 5.1. Jiný superuživatel než `wikiuser`: `POSTGRES_USER=…`; Podman: `ENGINE=podman`. Skript přenese role (s hesly), všechny databáze a řádky do `19/docker` vedle `18/docker`; nový cluster i každá databáze dostanou kolaci builtin `C.UTF-8` a indexy se postaví znovu. Porovná role, rozšíření, přesný počet řádků každé tabulky a pořadí vzorku slov v každé databázi (musl i builtin řadí podle kódových bodů, takže se shoduje), spustí ANALYZE a starý cluster jen přečte. Druhý běh odmítne, přerušený běh nic nepoškodí (podrobnosti v [deploy/postgres](../deploy/postgres/README.md#upgrade-svazku-postgresql-18--19)). Potřebuje na svazku volné místo velikosti dat; 0,7 GB produkčních dat trvalo 50 s.
4. **Atlas 5.1:** nový `docker-compose.yml` (pojistka a kolace jsou v něm; samotná výměna `ATLAS_POSTGRES_IMAGE` ve starém souboru nestačí) a `ATLAS_IMAGE`, `docker compose up -d`. Atlas 5.0 na PostgreSQL 19 běží také, databázi lze tedy povýšit i dřív než Atlas.

**Návrat:** `docker compose stop`, `ATLAS_POSTGRES_IMAGE` a `ATLAS_IMAGE` zpět na předchozí verze, `docker compose up -d`. PostgreSQL 18 použije nedotčený `18/docker`; co se mezitím zapsalo do 19, tam není. Až je 19 ověřená, místo uvolní `docker run --rm -v atlas_wiki-db-data:/v alpine rm -rf /v/18`.

Nastavení v datovém adresáři (`pg_hba.conf`, `postgresql.conf`, `ALTER SYSTEM`) se nepřenáší; skript na vlastní pravidla `pg_hba.conf` a `ALTER SYSTEM` upozorní. **Vlastní nebo spravovaná PostgreSQL** se povyšuje postupem správce (`pg_upgrade` nebo dump a obnova) s pgvectorem ≥ 0.8.1 pro 19. Atlas hledá s konfigurací `simple`; řazení, `lower`/`upper`, `to_tsvector`, `unaccent` a trigramy dávají na 18.6 a 19.0 (musl) i na 19.0 s builtin `C.UTF-8` (Debian) shodné výsledky (ověřeno na kopii produkce, všech 100 neprázdných textových sloupců), uložené vektory hledání proto zůstávají platné.

### Kolace databáze

Databáze Atlasu řadí text kolací **builtin `C.UTF-8`** (PostgreSQL 17+): podle kódových bodů Unicode s vlastními tabulkami PostgreSQL, nezávisle na knihovně C a ICU v image. Je to totéž pořadí, jaké dával musl v dřívějších Alpine image (`A B Z _x a b … Á Č č Ř Ž`), takže se pro uživatele nic nemění, a verze kolace se nikdy nemění, takže aktualizace image nevyžaduje REINDEX. Řazení pro lidi (abecední strom stránek a podobně) dělá Atlas v .NET česky, ne databáze. Kolaci určuje vznik clusteru: `docker-compose.yml` předává `POSTGRES_INITDB_ARGS=--locale-provider=builtin --builtin-locale=C.UTF-8` (platí jen pro nový svazek), `upgrade.sh` ji nastaví při přenosu a testy si ji zakládají samy (`TestDatabases.cs`). Ověření: `SELECT datname, datlocprovider, datlocale FROM pg_database;` (`b`, `C.UTF-8`).

Databáze s kolací knihovny C (`datlocprovider = c`: svazek z Alpine image otevřený Debian image, spravovaná PostgreSQL, novější glibc) může po změně knihovny řadit jinak, než jsou postavené její indexy. Atlas proto při každém startu, ještě před migracemi, porovná `pg_database.datcollversion` s `pg_database_collation_actual_version()`. Když se liší (na musl je verze prázdná), pod advisory lockem v jedné transakci přebuduje všechny indexy databáze (`REINDEX INDEX`, na kopii produkce 509 indexů za 0,65 s) a zapíše aktuální verzi (`ALTER DATABASE … REFRESH COLLATION VERSION`; z prázdné verze to PostgreSQL neumí, zapíše ji tedy superuživatel přímo do `pg_database`). Do logu napíše „The collation of database … changed … rebuilding N indexes“ a „Rebuilt N indexes …“; další start už nic nedělá. Když role vlastníka (`ConnectionStrings:atlas-db-owner`) není vlastníkem databáze ani superuživatelem, Atlas nic nepřestavuje a zapíše chybu; správce pak spustí `REINDEX DATABASE <db>;` a `ALTER DATABASE <db> REFRESH COLLATION VERSION;` (z prázdné verze jako superuživatel `UPDATE pg_database SET datcollversion = pg_database_collation_actual_version(oid) WHERE datname = '<db>';`). Pořadí textu takové databáze se tím nezmění; na builtin ji převede jen dump a obnova do databáze s builtin kolací.

### Upgrade na 2.0

Atlas 2.0 vyžaduje v databázi rozšíření **pgvector 0.8.1 nebo novější** (migrace 2026100520). Bez něj migrace skončí chybou „Atlas 2.0 vyžaduje PostgreSQL s rozšířením pgvector…“, kterou server zapíše do logu a vrací jako chybu databáze (`/api/setup/status`, odpovědi 503), dokud rozšíření nepřibude; celá dávka migrací se vrátí, data zůstanou beze změny a starší verze na nich dál běží. Postup:

1. **Záloha** podle předchozí části (databáze i `/data`).
2. **Výměna image databáze.** V Compose nastavit `ATLAS_POSTGRES_IMAGE=ghcr.io/scalesoft/atlas-postgres:18.6-pgvector0.8.7-r2` (výchozí hodnota `docker-compose.yml` od 2.0) a spustit jen databázi: `docker compose up -d wiki-db`. Image je `postgres:18.6-alpine` s přidaným pgvectorem: stejná knihovna C (musl) a tedy stejné řazení textu, stejná hlavní verze PostgreSQL a stejný svazek `wiki-db-data`. Žádný dump, obnova ani REINDEX. Běžící Atlas 1.x s novým image funguje beze změny. (Přechod na Debian image `pgvector/pgvector` s výchozí kolací glibc by změnil řazení textu; od 5.1 Atlas používá oficiální image s kolací builtin, viz [Kolace databáze](#kolace-databáze).)
3. **Atlas 2.0.** Nasadit nový `ATLAS_IMAGE`. Migrace vytvoří rozšíření (`CREATE EXTENSION IF NOT EXISTS vector`) a ověří jeho verzi.

**Spravovaná PostgreSQL** (bez práva vytvářet rozšíření): správce nainstaluje pgvector ≥ 0.8.1 a před nasazením 2.0 v databázi Atlasu spustí `CREATE EXTENSION vector WITH SCHEMA public;`. Starší verzi povýší `ALTER EXTENSION vector UPDATE;`. Celý postup (1.x na `postgres:18.6-alpine` → výměna image na stejném svazku → 2.0) dokazuje test upgradu níže.

### Test upgradu dat (vývojový nástroj)

Vývojový nástroj, není součástí CI ani vydání: u změny, která sahá na existující data, ověří, že data nasazené verze přejdou do nového buildu (Podman, jeden příkaz, ~15 min). Automatickou pojistkou jsou testy migrací v serverové sadě, které běží v CI.

```powershell
git fetch origin --tags
npm --prefix tests/browser ci
$deployedRef = '<release tag nebo SHA skutečně nasazené verze>'
pwsh tests/upgrade/upgrade-podman.ps1 -OldRef $deployedRef
```

Skript vytvoří dočasný `git worktree` staré verze (povinný `-OldRef`; hlavní pracovní strom nemění), publikuje ji i aktuální pracovní strom v package režimu editoru a v jednom podu spustí image databáze staré verze (`-OldPostgresImage`, výchozí je image, který připíná její `docker-compose.yml`, tedy pro 5.0.0 publikovaný `ghcr.io/scalesoft/atlas-postgres:18.6-pgvector0.8.7-r2` jako v produkci; před 2.0 `postgres:18.6-alpine`) na pojmenovaném volume. Starý server nainstaluje vlastní `first-run.spec.ts` a `tests/upgrade/seed-old.mjs` přes jeho veřejné API a skutečný prohlížeč naplní účty, skupiny (i odebrané členství), prostory s přímými, skupinovými, změněnými a odebranými granty, strom stránek s koncepty, publikacemi, historií, přejmenováním, obnovou verze, přesunem i mezi prostory, koš, přílohy, štítky, ikonu, oblíbené, sdílené odkazy (aktivní, vypršelý, zrušený), import Wiki.js fixture, neuloženou společnou editaci vázanou na staré schéma spolupráce a zálohu (ZIP). Stav zapíše do `manifest.json`. Pak starý server zastaví, na stejném svazku vymění image databáze za ten, který připíná `docker-compose.yml` (`-PostgresImage`); při jiné hlavní verzi PostgreSQL (18 → 19) mezi tím svazek povýší `deploy/postgres/upgrade.sh` jako v produkci. Ověří, že se žádná tabulka nezměnila, databáze má kolaci builtin `C.UTF-8` a všechny B-tree indexy jsou podle amcheck platné. Na stejných datech spustí nový server a ověří: všechny migrace nového stromu jsou zapsané, rozšíření pgvector ≥ 0.8.1, prázdnou indexační frontu, bajtově shodné odpovědi `GET /api/search` (AI-24), starý obsah všech tabulek (ve starých sloupcích) je po migraci bitově stejný, invarianty nových migrací (žádné `OwnAccess`, nula `ResourceGrant`, legacy granty bez platnosti, žádný zpětný audit), shodu `EffectivePageAccess` se starými granty, každou položku manifestu přes nové API včetně rozhodnutí o přístupu pod každým účtem, přechod stránky ze starého schématu spolupráce na aktuální (s `openapi`) bez ztráty neuložených změn, obnovu staré zálohy (`add` i `restore`) a nový import Wiki.js. Nakonec spustí vybrané `tests/browser` specy proti upgradované instanci (`-SmokeSpec`, `-SkipSmoke`). Důkazy a `summary.json` jsou v `artifacts/upgrade-podman/<run>/`; pod, volume, tajemství a worktree se odstraní (`-Keep` je ponechá). Jiná nasazená verze: `-OldRef <sha>`; již publikované výstupy: `-OldPublishDir`, `-NewPublishDir`.

Hodnotu `$deployedRef` nahraďte tagem skutečně běžícího image nebo jeho OCI labelem `org.opencontainers.image.revision` (běžící instance ho hlásí na `/api/setup/status` jako `<verze>+<commit>`). Publikovaný image lze místo sestavení pracovního stromu předat parametrem `-NewAppImage atlas:<verze>` (po `podman pull`); pracovní strom pak musí být na vydávaném commitu, protože z něj pocházejí ověřovací skripty.

## Lokální testování a spolupráce editoru

Aspire nastavuje `Authentication:Builtin:AllowDevelopmentPasswords=true`. Pouze v prostředí `Development` to dovoluje hesla od čtyř znaků; v ostatních prostředích zůstává minimum dvanáct znaků. Wizard načítá skutečné minimum ze serveru. Aplikace žádný testovací účet automaticky nezakládá.

Stávající editor používá veřejný SignalR adaptér a knihovnu `src/Atlas.Markdown.Sync`. Hub `/hubs/markdown` běží ve stejném .NET procesu. PostgreSQL uchovává potvrzené Yjs změny a snapshot, zatímco explicitní uložení vytváří pracovní revizi a publikování odkazuje na konkrétní revizi. Historie umí readonly náhled a obnovu konceptu. Podrobnosti včetně práv, zneplatnění staré relace a omezení V1 jsou v [architektuře](architecture.md).

## Osobní API

WikiAdmin musí používání API výslovně povolit konkrétnímu účtu; výchozí stav je vypnutý i pro WikiAdmin. V **Profil → API** uživatel vytvoří pojmenovaný token `read` nebo `read` + `write` na 1–365 dní, nejvýše dvacet současně platných tokenů. Tajná hodnota se zobrazí jednou, databáze ukládá pouze SHA-256 otisk; profil vrací metadata posledních sto tokenů. Vypnutí API odvolá dosavadní tokeny a opětovné povolení je neobnoví.

`/api/v1` vyžaduje `Authorization: Bearer <osobní-token>`; samotná session cookie nestačí. Prohlížečové `/api` a správa tokenů používají cookie, mutace také CSRF. ID token firemního OIDC není osobní API token. Každý požadavek kontroluje aktuální účet, security stamp, platnost tokenu, API povolení a scope; obsah navíc současná práva prostoru a soukromého konceptu. `write` nezvýší Reader na Writer ani neotevře administraci účtů, grantů či dalších tokenů.

| Operace | Stránkování a chování |
| --- | --- |
| Prostory a stránky prostoru | `offset` 0–100000, `limit` 1–100 (výchozí 50), `hasMore`; prostory název/ID, stránky pořadí stromu/ID |
| Hledání | `q`, volitelné `spaceId`, `offset` 0–1000, `limit` 1–50 (20), `nextOffset` a `hasMore`; pouze publikované revize |
| Historie | Writer/Admin, sestupné verze, exkluzivní `beforeVersion`, `limit` 1–50 (20), `nextBeforeVersion` |
| Osobní knihovna | `favorites`, `recent`, `drafts`, `updates`; `offset` 0–1000, `limit` 1–50 (20); zápis oblíbené/návštěvy mění jen vlastní metadata |
| Obsah, publikace, strom a koš | Stejné slices a ACL jako UI; zápisy vyžadují odpovídající roli a CAS. Obsah stránky se zapisuje jen jako Markdown (`markdown`, celý text se značkami `⟦c:N⟧` z `GET /pages/{id}?format=markdown`); `contentJson` odmítne 400 `content_json_not_accepted`, viz [MCP a API](mcp-claude-code.md#vkládání-obrázků-a-příloh-do-stránky) |
| Přílohy a ZIP export | Multipart upload, autorizované binární GET/HEAD a export článku/prostoru; podrobnosti níže |

Časy v JSON jsou UTC; interval `ActiveTo=null` znamená současnou verzi, UI čas převádí lokálně. Uložení a obnova používají `expectedVersion`, organizace stromu vlastní `expectedTreeVersion`. Konflikt 409 se nesmí řešit slepým přepsáním nové verze. Obnova vytváří koncept a nemění publikaci. Aplikační chyby mají Problem Details (`status`, `title`, případně `code` nebo bezpečné `traceId`); klient musí zvládnout i prázdné frameworkové 404/429. Důležité stavy: 400 validace, 401 token, 403 scope/globální právo, 404 nedostupný obsah, 409 konflikt, 413 limit, 429 frekvence a 503 dočasná nedostupnost. Automaticky neopakovat mutaci při libovolném 409.

Běžné v1 endpointy mají limit 120 požadavků za minutu na token. Hledání má vlastní zásobník 30 požadavků s doplněním deseti za pět sekund na uživatele; ZIP export vlastní limit dvou souběžných přenosů. `Retry-After` není současnou implementací zaručené. Zrušené požadavky předávají cancellation do služby/DB; odpojení klienta není aplikační chyba zapisovaná s tělem požadavku do logu.

Příloha se nahrává jako multipart `file` a `kind=image|file`: nejvýše 25 MiB soubor, 10 MiB obrázek a 26 MiB celý požadavek. Inline obrázky jsou PNG/JPEG/GIF/WebP s ověřenou signaturou; ostatní soubory se stahují jako `application/octet-stream` s bezpečným názvem. GET/HEAD podporuje rozsahy a ETag, vždy ověřuje práva; Reader získá jen soubor odkazovaný současnou publikací. Odpovědi jsou soukromé `no-store` a `nosniff`.

`GET /api/v1/pages/{id}/export` nebo `/spaces/{id}/export` vrací `application/zip`: uložený rich JSON, Markdown, oprávněné přílohy a manifest s cestami podle serverových ID a SHA-256 každého obsahového souboru. Celý ZIP má ETag včetně manifestu. Limit je 256 MiB nekomprimovaných dat, 500 stránek a 2000 položek ZIP; při 413 zúžit export. Server zapisuje do soukromého dočasného souboru a streamuje jej, neuchovává celý archiv v RAM. Snapshot databáze doplňuje kontrola hashů blobů a opětovná kontrola aktuálních ACL a bearer tokenu před odesláním. Již doručené bajty nelze po odvolání práva stáhnout zpět.

OpenAPI se generuje ze skutečných endpointů včetně DTO, multipart požadavků, binárních MIME a odpovědí 200/206. Profil stáhne stejný dokument přes `/api/profile/api-document`; integrační klient přes `/api/v1/openapi.json` s bearer tokenem. Příklad používá pouze zástupnou hodnotu:

```sh
curl --header 'Authorization: Bearer <osobní-token>' \
  'https://wiki.example.internal/api/v1/spaces?limit=50'
```
