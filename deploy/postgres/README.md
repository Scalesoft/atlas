# Image databáze `atlas-postgres`

`atlas-postgres:18.6-pgvector0.8.7-r2` je oficiální `postgres:18.6-alpine` (připnutý digestem) s přidaným rozšířením [pgvector](https://github.com/pgvector/pgvector), které Atlas 2.0 vyžaduje (migrace 2026100520). Nic jiného se nemění.

## Proč vlastní image

- **Stejná data bez zásahu.** Instalace Atlasu do verze 1.x běžely na `postgres:18-alpine`. Stejný základ znamená stejnou knihovnu C (musl), a tedy stejné řazení textu, a stejnou hlavní verzi PostgreSQL. Existující svazek se použije beze změny, bez dumpu a bez REINDEX. Oficiální `pgvector/pgvector` je Debian (glibc): řadí text jinak (`collation.sql`: musl `A B Z _x a b … Č č`, glibc `a A ä b B c č Č …`), takže by textové B-tree indexy po přechodu neodpovídaly.
- **Přenositelná binárka.** `make OPTFLAGS=""` vypne `-march=native`; pgvector si instrukce SIMD vybírá za běhu. `with_llvm=no` vynechá bitkód pro JIT.
- **Ověřený zdroj.** Tag `v<PGVECTOR_VERSION>` musí ukazovat na připnutý commit `PGVECTOR_COMMIT`, jinak stavba skončí. Do výsledné vrstvy jdou jen `vector.so`, `vector.control` a `vector--*.sql` (včetně skriptů pro `ALTER EXTENSION vector UPDATE`).

Databáze v testech CI používá tento image (`atlas-postgres`, Alpine, musl, sestavený z tohoto `Dockerfile`), takže řadí text jako vydaný image. Lokální testovací databáze a Aspire používají oficiální multiarch `pgvector/pgvector:0.8.7-pg18-trixie` (Debian, glibc, jako dřívější `postgres:18.6`, svazek Aspire zůstává); řadí text jinak než CI a vydaný image, test závislý na pořadí proto může lokálně projít a na CI spadnout. Podman e2e a test upgradu používají tento image, protože ověřují přesně databázi z `docker-compose.yml`.

## Tag a verze

Tag se skládá z výchozích hodnot `ARG` v `Dockerfile`: `<POSTGRES_VERSION>-pgvector<PGVECTOR_VERSION>[-<IMAGE_REVISION>]`. Tag má odpovídat jednomu obsahu `Dockerfile` (git blob je ve štítku `org.atlas.postgres.dockerfile`, když ho stavba předá v `DOCKERFILE_BLOB`): každá jiná změna `Dockerfile` proto zvýší `IMAGE_REVISION` (`r2`, `r3`, …), nová verze PostgreSQL nebo pgvectoru změní tag sama. Tentýž tag má být výchozí hodnotou `ATLAS_POSTGRES_IMAGE` v `docker-compose.yml`. Bezpečnostní aktualizace Alpine: nový digest v `POSTGRES_BASE` a vyšší `IMAGE_REVISION`.

## Sestavení a ověření

Image v registru `ghcr.io/scalesoft/atlas-postgres:<tag>` publikuje krok „Database image“ ve workflow vydání při každém vydání, pro `linux/amd64`. Na hostiteli jiné architektury se varianta překládá a ověřuje v emulaci; jde o krátký překlad C, ne o .NET SDK (AGENTS.md). Ověření (`verify.sh`) pro zvolenou platformu kontroluje OS a architekturu, verze PostgreSQL a pgvectoru, `CREATE EXTENSION vector`, kosinovou vzdálenost, HNSW index s `hnsw.iterative_scan` (`verify.sql`) a shodné řazení textu se základním image (`collation.sql`).

Místně (Podman, výsledek `localhost/atlas-postgres:<tag>`; e2e a test upgradu ho staví samy):

```powershell
pwsh deploy/postgres/build.ps1                                   # linux/amd64
$env:ENGINE = 'podman'; bash deploy/postgres/verify.sh localhost/atlas-postgres:18.6-pgvector0.8.7-r2 linux/amd64
```

Výměnu image u existující instalace popisuje [docs/operations.md](../../docs/operations.md#upgrade-na-20), „Upgrade na 2.0“.
