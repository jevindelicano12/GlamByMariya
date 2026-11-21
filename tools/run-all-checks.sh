#!/bin/bash
# Runs every local test: all WorkflowChecks sections, the other local test projects,
# and the Docker database checks (needs Docker Desktop running). One line each.
cd "$(dirname "$0")/.." || exit 1
dotnet build tests/WorkflowChecks.csproj 2>&1 | grep -E " error " | head -5
fail=0
for a in $(grep -o 'args.Contains("--[a-z-]*")' tests/Program.cs | sed 's/args.Contains("\(.*\)")/\1/' | sort -u); do
  out=$(dotnet run --project tests/WorkflowChecks.csproj --no-build -- $a 2>&1); code=$?
  [ $code -ne 0 ] && fail=1
  echo "[$a] exit=$code $(echo "$out" | grep -E "checks passed|Unhandled" | tail -1 | cut -c1-120)"
done
for p in RoleChecks ProofStorageChecks DemoCatalogSnapshotChecks; do
  out=$(dotnet run --project tests/$p 2>&1); code=$?
  [ $code -ne 0 ] && fail=1
  echo "[$p] exit=$code $(echo "$out" | grep -E "passed|Unhandled|FAIL" | tail -1 | cut -c1-100)"
done
out=$(tools/test-db.sh tests/DatabaseChecks 2>&1); code=$?
[ $code -ne 0 ] && fail=1
echo "[DatabaseChecks] exit=$code $(echo "$out" | grep -E "checks passed|Unhandled|FAIL" | tail -3 | tr '\n' ' ' | cut -c1-200)"
echo "OVERALL: $([ $fail -eq 0 ] && echo PASS || echo FAIL)"
