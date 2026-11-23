#!/usr/bin/env bash
# Acceptance of an atlas-postgres image for one platform: OS/architecture, PostgreSQL and pgvector versions from the
# Dockerfile, CREATE EXTENSION vector with a cosine HNSW scan (verify.sql), and the same text ordering as the base image
# (collation.sql), i.e. text sorts as on the earlier Alpine (musl) images. Runs each image as that platform, so on the
# ARM64 host the linux/amd64 variant runs emulated: that is its runtime acceptance, not the native ARM64 run.
#   deploy/postgres/verify.sh <image> <platform>            e.g. atlas-postgres-ci:abc-amd64 linux/amd64
#   ENGINE=podman deploy/postgres/verify.sh localhost/atlas-postgres:19.0-pgvector0.8.7-r1 linux/amd64
set -euo pipefail
image=$1
platform=$2
engine=${ENGINE:-docker}
here=$(cd "$(dirname "$0")" && pwd)
arg() { sed -n "s/^ARG $1=//p" "$here/Dockerfile" | head -n 1; }
base=$(arg POSTGRES_BASE)
expected_postgres=$(arg POSTGRES_VERSION)
expected_pgvector=$(arg PGVECTOR_VERSION)

actual_platform=$("$engine" image inspect "$image" --format '{{.Os}}/{{.Architecture}}')
[[ "$actual_platform" == "$platform" ]] || { echo "$image is $actual_platform, expected $platform" >&2; exit 1; }

# Named, so the EXIT trap can remove them (a container id captured by $(...) would never reach the parent shell's list).
run_id="atlas-postgres-verify-$$"
cleanup() { "$engine" rm -f "$run_id-candidate" "$run_id-reference" > /dev/null 2>&1 || true; }
trap cleanup EXIT
start() {
  local container="$run_id-$1"
  "$engine" run -d --name "$container" --platform "$platform" -e POSTGRES_PASSWORD=verify "$2" > /dev/null
  for _ in $(seq 1 240); do
    # TCP, not the socket: the entrypoint's temporary init server listens on the socket only.
    if "$engine" exec "$container" pg_isready -h 127.0.0.1 -U postgres > /dev/null 2>&1; then return; fi
    sleep 0.5
  done
  "$engine" logs "$container" >&2
  echo "PostgreSQL in $2 ($platform) did not become ready" >&2
  return 1
}
psql_file() { "$engine" exec -i "$1" psql -X -q -At -v ON_ERROR_STOP=1 -U postgres < "$2"; }

start candidate "$image"
candidate="$run_id-candidate"
"$engine" exec "$candidate" sh -c 'test "$(uname -m)" = "$1"' _ "$(case "$platform" in linux/amd64) echo x86_64 ;; linux/arm64) echo aarch64 ;; esac)" \
  || { echo "the container does not run as $platform" >&2; exit 1; }
result=$(psql_file "$candidate" "$here/verify.sql")
echo "$result"
grep -qx "postgres=$expected_postgres" <<< "$result" || { echo "expected PostgreSQL $expected_postgres" >&2; exit 1; }
grep -qx "pgvector=$expected_pgvector" <<< "$result" || { echo "expected pgvector $expected_pgvector" >&2; exit 1; }

start reference "$base"
reference="$run_id-reference"
ours=$(psql_file "$candidate" "$here/collation.sql")
theirs=$(psql_file "$reference" "$here/collation.sql")
echo "$ours"
[[ "$ours" == "$theirs" ]] || { printf 'text ordering differs from %s:\n%s\n' "$base" "$theirs" >&2; exit 1; }
echo "OK: $image as $platform"
