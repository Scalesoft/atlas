# Image databáze `atlas-postgres`

`atlas-postgres:19.0-pgvector0.8.7-r1` je oficiální `postgres:19.0-alpine` (připnutý digestem) s přidaným rozšířením [pgvector](https://github.com/pgvector/pgvector), které Atlas 2.0 vyžaduje (migrace 2026100520), a s pojistkou startu (níže). Nic jiného se nemění. Atlas do 5.0 používal `atlas-postgres:18.6-pgvector0.8.7-r2` (PostgreSQL 18).

## Proč vlastní image

- **Stejné řazení textu.** Základ je od začátku Alpine (musl), takže text řadí stejně jako dřívější `postgres:18-alpine` a image 18.x (`collation.sql`: musl `A B Z _x a b … Č č`, glibc `a A ä b B c č Č …`). Oficiální `pgvector/pgvector` je Debian (glibc) a řadí jinak. Ověřeno i pro 18.6 → 19.0: řazení, `to_tsvector('simple')`, `unaccent`, `show_trgm` a změna velikosti písmen dávají na obou stejný výsledek, takže uložené `tsvector` hledání platí dál (bod 20 poznámek k vydání 19 se Atlasu netýká).
- **Nová hlavní verze se svazkem nesdílí data.** Svazek je `/var/lib/postgresql` a každá hlavní verze má svůj cluster v `<verze>/docker`. Data PostgreSQL 18 (`18/docker`) přenese `upgrade.sh` do `19/docker` a původní ponechá. Pojistka startu (`atlas-entrypoint.sh` v `Dockerfile`): když je `19/docker` prázdný a na svazku je cluster jiné verze, kontejner skončí chybou a odkáže na upgrade. Bez ní by základní image vedle starých dat založil prázdnou databázi a Atlas by nabídl novou instalaci.
- **Přenositelná binárka.** `make OPTFLAGS=""` vypne `-march=native`; pgvector si instrukce SIMD vybírá za běhu. `with_llvm=no` vynechá bitkód pro JIT.
- **Ověřený zdroj.** Tag `v<PGVECTOR_VERSION>` musí ukazovat na připnutý commit `PGVECTOR_COMMIT`, jinak stavba skončí. Do výsledné vrstvy jdou jen `vector.so`, `vector.control` a `vector--*.sql` (včetně skriptů pro `ALTER EXTENSION vector UPDATE`).

Databáze v testech CI používá tento image (`atlas-postgres`, Alpine, musl, sestavený z tohoto `Dockerfile`), takže řadí text jako vydaný image. Lokální testovací databáze a Aspire používají oficiální multiarch `pgvector/pgvector:0.8.7-pg19-trixie` (Debian, glibc; Aspire má svazek `atlas-postgres-19-data`, starý `atlas-postgres-18-data` nechává beze změny); řadí text jinak než CI a vydaný image, test závislý na pořadí proto může lokálně projít a na CI spadnout. Podman e2e a test upgradu používají tento image, protože ověřují přesně databázi z `docker-compose.yml`.

## Tag a verze

Tag se skládá z výchozích hodnot `ARG` v `Dockerfile`: `<POSTGRES_VERSION>-pgvector<PGVECTOR_VERSION>[-<IMAGE_REVISION>]`. Tag má odpovídat jednomu obsahu `Dockerfile` (git blob je ve štítku `org.atlas.postgres.dockerfile`, když ho stavba předá v `DOCKERFILE_BLOB`): každá jiná změna `Dockerfile` proto zvýší `IMAGE_REVISION` (`r2`, `r3`, …), nová verze PostgreSQL nebo pgvectoru změní tag sama. Tentýž tag má být výchozí hodnotou `ATLAS_POSTGRES_IMAGE` v `docker-compose.yml`. Bezpečnostní aktualizace Alpine: nový digest v `POSTGRES_BASE` a vyšší `IMAGE_REVISION`.

## Sestavení a ověření

Image v registru `ghcr.io/scalesoft/atlas-postgres:<tag>` publikuje krok „Database image“ ve workflow vydání při každém vydání, pro `linux/amd64`. Na hostiteli jiné architektury se varianta překládá a ověřuje v emulaci; jde o krátký překlad C, ne o .NET SDK (AGENTS.md). Ověření (`verify.sh`) pro zvolenou platformu kontroluje OS a architekturu, verze PostgreSQL a pgvectoru, `CREATE EXTENSION vector`, kosinovou vzdálenost, HNSW index s `hnsw.iterative_scan` (`verify.sql`) a shodné řazení textu se základním image (`collation.sql`).

Místně (Podman, výsledek `localhost/atlas-postgres:<tag>`; e2e a test upgradu ho staví samy):

```powershell
pwsh deploy/postgres/build.ps1                                   # linux/amd64
$env:ENGINE = 'podman'; bash deploy/postgres/verify.sh localhost/atlas-postgres:19.0-pgvector0.8.7-r1 linux/amd64
```

## Upgrade svazku PostgreSQL 18 → 19

`upgrade.sh <svazek> <starý image> <nový image>` přenese cluster 18 do 19 výpisem a obnovou (`pg_dumpall` a `psql` z nového image) do `19/upgrade`, porovná role (včetně hesel), rozšíření a přesný počet řádků každé tabulky ve všech databázích, spustí ANALYZE a teprve čistě zastavený cluster přejmenuje na `19/docker`. `18/docker` jen čte (PostgreSQL 18 na něm nastartuje a čistě se zastaví), takže návrat je výměna image zpět. Odmítne běžet, když svazek používá kontejner, když `19/docker` už existuje nebo když na svazku není místo na druhou kopii. Přerušený běh nechá jen `19/upgrade`, který další běh smaže. Rozšíření vzniknou ve výchozí verzi PostgreSQL 19 (contrib `btree_gist` 1.8 → 1.9; pgvector zůstává 0.8.7). Nastavení uložená v datovém adresáři (`pg_hba.conf`, `postgresql.conf`, `ALTER SYSTEM`) se nepřenáší; skript na odlišná pravidla a `ALTER SYSTEM` upozorní.

Postup pro instalaci z `docker-compose.yml` popisuje [docs/operations.md](../../docs/operations.md#upgrade-na-postgresql-19), „Upgrade na PostgreSQL 19“. Dřívější výměnu image u instalací 1.x popisuje „Upgrade na 2.0“ tamtéž.
