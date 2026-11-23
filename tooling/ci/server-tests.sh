#!/usr/bin/env bash
# Server, Markdown sync, Yjs and YouTrack tests of .github/workflows/ci.yml (every pull request, develop, and main before
# release.yml tags and publishes anything). The batches keep each test process small and the failure diagnostics intact.
#
# Expects: ATLAS_TEST_CONNECTION/ATLAS_TEST_DATABASE pointing at a throwaway PostgreSQL with pgvector (the image docker-compose.yml pins),
# the .NET SDK from global.json. Runs on a GitHub-hosted ubuntu-latest runner.
set -euo pipefail

: "${RUNNER_TEMP:?Set RUNNER_TEMP to the temporary directory of the job}"

echo "Native test host: $(uname -sm); release image target: linux/amd64 (linux-x64)"
dotnet --info
free -m
cat /sys/fs/cgroup/memory.max /sys/fs/cgroup/memory.events 2>/dev/null || true

# Preserve evidence when the test host is killed before VSTest can write a crash dump.
memory_sampler=''
on_exit() {
  local code=$?
  if [[ -n "$memory_sampler" ]]; then
    kill "$memory_sampler" 2>/dev/null || true
    wait "$memory_sampler" 2>/dev/null || true
  fi
  print_timings
  if ((code != 0)); then
    free -m
    cat /sys/fs/cgroup/memory.events /sys/fs/cgroup/memory.peak 2>/dev/null || true
    tail -n 240 "$RUNNER_TEMP/server-memory.log" || true
    grep -E "exited with|Exit code|exit code" "$RUNNER_TEMP"/server-*.log || true
  fi
  exit "$code"
}
trap on_exit EXIT

# How long each part took and what the shared fixtures cost each test process (databases cloned, reset and dropped, hosts started:
# tests/Atlas.Server.Tests/Platform/TestCosts.cs). Printed at the end and after a failure, so a part that grows shows in the log.
timings=()
timed() {
  local name=$1 started=$SECONDS code=0
  shift
  "$@" || code=$?
  timings+=("$(printf '%5ss  %s%s' "$((SECONDS - started))" "$name" "$( ((code)) && echo ' (failed)')")")
  return "$code"
}
print_timings() {
  echo 'Durations:'
  printf '%s
' "${timings[@]}"
  echo 'Fixture costs per test process:'
  cat "$RUNNER_TEMP"/server-costs.* 2>/dev/null || true
}
export ATLAS_TEST_COSTS="$RUNNER_TEMP/server-costs"
# The PostgreSQL container of this job is thrown away with it: the fixtures turn its fsync off (TestDatabases).
export ATLAS_TEST_DISPOSABLE_SERVER=1

# Results (trx) and crash dumps of every dotnet test below land here; the workflow uploads them when the job fails.
results=(--results-directory "$RUNNER_TEMP/trx" --logger trx)

server=tests/Atlas.Server.Tests
dotnet restore "$server"
timed 'server build' dotnet build "$server" --no-restore -c Release

# Observe total resident memory without constraining the GC heap.
(while true; do date -u +%FT%TZ; grep MemAvailable /proc/meminfo; ps -eo pid,ppid,comm,rss,nlwp --sort=-rss | sed -n '1,10p'; sleep 1; done) > "$RUNNER_TEMP/server-memory.log" &
memory_sampler=$!

# Fresh processes release the memory the test hosts keep: a process keeps about 6 MB per handler host and 19-25 MB per HTTP site
# it ever started (docs/testing/test-speed-4.1.0.md; one process over the whole suite reached 22.8 GB).
# Disjoint prefixes plus their complement cover every test, including new ones. The batches follow the measured test time and
# the number of hosts: A and B (483 and 335 tests, the most HTTP sites) run apart, as P does (755 tests); N and O are small and
# run with the remainder. The cost line of each process below prints its peak memory: split the batch that grows.
remaining=''
for letters in A B CD EFGHIJKLM P QRS; do
  filter=''
  for ((i = 0; i < ${#letters}; i++)); do
    prefix="Atlas.Server.Tests.${letters:i:1}"
    filter+="|FullyQualifiedName~$prefix"
    remaining+="&FullyQualifiedName!~$prefix"
  done
  echo "Server batch $letters"
  timed "server batch $letters" dotnet test "$server" --no-build -c Release --filter "${filter#|}" --blame-crash --blame-crash-dump-type mini --diag "$RUNNER_TEMP/server-$letters.log" "${results[@]}"
done
timed 'server batch remaining' dotnet test "$server" --no-build -c Release --filter "${remaining#&}" --blame-crash --blame-crash-dump-type mini --diag "$RUNNER_TEMP/server-remaining.log" "${results[@]}"

markdown=tests/Atlas.Markdown.Sync.Tests
dotnet restore "$markdown"
timed 'Markdown sync tests' dotnet test "$markdown" --no-restore -c Release "${results[@]}"

yjs=tests/Atlas.Yjs.Tests
dotnet restore "$yjs"
timed 'Yjs tests' dotnet test "$yjs" --no-restore -c Release "${results[@]}"

youtrack=tests/Atlas.YouTrack.Tests
dotnet restore "$youtrack"
timed 'YouTrack tests' dotnet test "$youtrack" --no-restore -c Release "${results[@]}"

echo 'Peak job memory (bytes):'
cat /sys/fs/cgroup/memory.peak 2>/dev/null || true
