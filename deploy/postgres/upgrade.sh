#!/usr/bin/env bash
# Moves the database volume of an Atlas installation to a new PostgreSQL major (18 -> 19; docs/operations.md, "Upgrade na
# PostgreSQL 19"). The volume is the database container's /var/lib/postgresql, where each major keeps its cluster in <major>/docker:
# - the old cluster (18/docker) is only read and stays as it was: switching back to the old image is the rollback;
# - the new image's pg_dumpall and psql copy every role (with its password), database and row into 19/upgrade;
# - roles, extensions (in the new major's default version, e.g. contrib btree_gist) and the exact row count of every table are
#   compared, then ANALYZE runs;
# - only a complete, verified and cleanly stopped cluster is renamed to 19/docker, which the new image opens.
# It refuses to run while a container uses the volume, when 19/docker exists (already upgraded) or without enough free space.
# An interrupted run leaves only 19/upgrade, which the next run removes. Take a backup first all the same.
#   deploy/postgres/upgrade.sh <volume> <old image> <new image>
#   e.g. deploy/postgres/upgrade.sh atlas_wiki-db-data ghcr.io/scalesoft/atlas-postgres:18.6-pgvector0.8.7-r2 ghcr.io/scalesoft/atlas-postgres:19.0-pgvector0.8.7-r1
#   POSTGRES_USER: the superuser of the volume (docker-compose.yml: wikiuser, the default); ENGINE=podman for Podman.
set -euo pipefail
export MSYS_NO_PATHCONV=1  # Git Bash on Windows would rewrite the container paths below
[[ $# -eq 3 ]] || { sed -n '2,13p' "$0" >&2; exit 2; }
volume=$1 old_image=$2 new_image=$3
engine=${ENGINE:-docker}
user=${POSTGRES_USER:-wikiuser}
root=/var/lib/postgresql
run="atlas-pg-upgrade-$$"
fail() { echo "upgrade: $*" >&2; exit 1; }
in_volume() { "$engine" run --rm -v "$volume:$root" --entrypoint sh "$new_image" -c "$1"; }
major() { "$engine" run --rm --entrypoint sh "$1" -c 'echo "$PG_MAJOR"'; }

"$engine" volume inspect "$volume" > /dev/null 2>&1 || fail "no volume $volume"
[[ -z $("$engine" ps -q --filter "volume=$volume") ]] || fail "a container uses $volume; stop it first (docker compose stop)"
old=$(major "$old_image") new=$(major "$new_image")
[[ $old =~ ^[0-9]+$ && $new =~ ^[0-9]+$ && $new -gt $old ]] || fail "expected an old and a newer PostgreSQL image, got $old and $new"
in_volume "test ! -e $root/$new/docker" || fail "$root/$new/docker exists: the volume is already upgraded"
in_volume "test -s $root/$old/docker/PG_VERSION" || fail "$volume has no PostgreSQL $old cluster in $root/$old/docker"
in_volume "rm -rf $root/$new/upgrade
  used=\$(du -sk $root/$old/docker | cut -f1); free=\$(df -Pk $root | awk 'NR==2 {print \$4}')
  echo \"PostgreSQL $old data: \$((used / 1024)) MB, free: \$((free / 1024)) MB\"; [ \"\$free\" -gt \"\$used\" ]" \
  || fail "not enough free space for a second copy of the data"

done=
cleanup() {
  "$engine" rm -f "$run-old" "$run-new" > /dev/null 2>&1 || true
  "$engine" volume rm -f "$run-socket" > /dev/null 2>&1 || true
  [[ -n $done ]] || in_volume "rm -rf $root/$new/upgrade" || true
}
trap cleanup EXIT
wait_ready() {
  for _ in $(seq 1 240); do
    # TCP for the new container: its entrypoint's temporary init server listens on the socket only.
    if "$engine" exec "$1" pg_isready -q -h "$2" -U "$user"; then return; fi
    sleep 0.5
  done
  "$engine" logs "$1" >&2
  fail "$1 did not become ready"
}

# Both containers mount the volume and share the old server's socket directory; no network, local (trust) connections only.
"$engine" volume create "$run-socket" > /dev/null
"$engine" run -d --name "$run-old" --network none -v "$volume:$root" -v "$run-socket:/run/postgresql" "$old_image" > /dev/null
wait_ready "$run-old" /run/postgresql
"$engine" run -d --name "$run-new" --network none -v "$volume:$root" -v "$run-socket:/old" -e PGDATA="$root/$new/upgrade" \
  -e POSTGRES_USER="$user" -e POSTGRES_DB=postgres -e POSTGRES_PASSWORD="$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')" \
  "$new_image" > /dev/null
wait_ready "$run-new" 127.0.0.1

echo "Copying PostgreSQL $old to $new ..."
# The superuser exists in the new cluster already: its CREATE ROLE fails as pg_dumpall intends, the ALTER ROLE after it applies.
"$engine" exec -e U="$user" "$run-new" sh -c 'set -o pipefail
  pg_dumpall -h /old -U "$U" | psql -X -q -U "$U" -d postgres 2> /tmp/restore.err > /dev/null
  status=$?; cat /tmp/restore.err >&2
  [ "$status" -eq 0 ] && ! grep -E "ERROR|FATAL|PANIC" /tmp/restore.err | grep -qvF "ERROR:  role \"$U\" already exists"' \
  || fail "the copy failed (above); $root/$old/docker is unchanged"

echo "Comparing roles, extensions and row counts ..."
# shellcheck disable=SC2016
inventory='SELECT format($q$SELECT %L || $s$=$s$ || count(*) FROM %I.%I$q$, n.nspname || $s$.$s$ || c.relname, n.nspname, c.relname)
  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE c.relkind IN ($s$r$s$, $s$m$s$) AND n.nspname NOT IN ($s$pg_catalog$s$, $s$information_schema$s$) AND n.nspname NOT LIKE $s$pg_toast%$s$
  ORDER BY 1 \gexec
SELECT $s$extension $s$ || extname FROM pg_extension ORDER BY 1;'
compare() {
  "$engine" exec -e U="$user" -e H="$1" -e Q="$inventory" "$run-new" sh -c '
    psql -X -At -v ON_ERROR_STOP=1 -h "$H" -U "$U" -d postgres -c "SELECT rolname || md5(row(rolsuper, rolcreaterole, rolcreatedb, rolcanlogin, rolreplication, rolbypassrls, rolpassword)::text) FROM pg_authid WHERE oid = 10 OR oid >= 16384 ORDER BY 1"
    for db in $(psql -X -At -v ON_ERROR_STOP=1 -h "$H" -U "$U" -d postgres -c "SELECT datname FROM pg_database WHERE datallowconn ORDER BY 1"); do
      echo "database $db"; printf "%s\n" "$Q" | psql -X -At -v ON_ERROR_STOP=1 -h "$H" -U "$U" -d "$db"
    done'
}
before=$(compare /old) after=$(compare /run/postgresql)
[[ "$before" == "$after" ]] || { diff <(echo "$before") <(echo "$after") >&2 || true; fail "the copy differs (above); $root/$old/docker is unchanged"; }
echo "$(grep -c '=' <<< "$before") tables with $(awk -F= '{s += $NF} END {print s}' <<< "$before") rows, the roles and extensions match in $(grep -c '^database ' <<< "$before") databases."
"$engine" exec "$run-new" vacuumdb -q -U "$user" --all --analyze-only

# Clean shutdowns (the images stop with SIGINT, a fast shutdown), then the new cluster takes its final place.
"$engine" stop -t 600 "$run-new" "$run-old" > /dev/null
for image in "$new_image $new/upgrade" "$old_image $old/docker"; do
  set -- $image
  "$engine" run --rm -v "$volume:$root" --entrypoint sh "$1" -c "pg_controldata $root/$2 | grep -q '^Database cluster state: *shut down$'" \
    || fail "$root/$2 did not shut down cleanly"
done
in_volume "mv $root/$new/upgrade $root/$new/docker"
done=1
# Settings kept in the data directory are not copied: name them (compose passes its settings on the command line).
in_volume "rules() { grep -v '^#' \"\$1\" | grep .; }
  [ \"\$(rules $root/$old/docker/pg_hba.conf)\" = \"\$(rules $root/$new/docker/pg_hba.conf)\" ] || echo 'upgrade: WARNING: the rules in $old/docker/pg_hba.conf differ from the new ones; carry over your own.' >&2
  grep -qv '^#' $root/$old/docker/postgresql.auto.conf 2>/dev/null && echo 'upgrade: WARNING: ALTER SYSTEM settings in $old/docker/postgresql.auto.conf were not carried over.' >&2
  true"
echo "OK: $volume runs PostgreSQL $new from $root/$new/docker; $root/$old/docker is kept for a rollback. Start the stack with $new_image."
