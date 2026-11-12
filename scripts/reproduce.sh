#!/usr/bin/env bash
# Reproduces every number in results/results.json:
#   1. alarm-load / fault-detection benchmark on the full TEP data set
#   2. API load test (indexed vs no history index) on this machine
#   3. test counts (.NET + Angular)
set -euo pipefail
cd "$(dirname "$0")/.."
out=results/results.json
mkdir -p results
scripts/get-data.sh data/tep-full
dotnet build Deadband.sln -c Release
cli="dotnet src/Deadband.Cli/bin/Release/net8.0/Deadband.Cli.dll"
api="src/Deadband.Api/bin/Release/net8.0/Deadband.Api.dll"

# 1. benchmark (tunes on training files, reports once on test files), then export the tuned configs
$cli evaluate --data data/tep-full --out "$out"
$cli export-configs --data data/tep-full --results "$out" --out config
$cli validate config/*.json

# 2. load test: two API instances with a seeded 300k-row alarm journal, one without the (Tag, TimeSec) index
tmp=$(mktemp -d)
for spec in "5082 true idx" "5083 false noidx"; do
  set -- $spec
  dotnet "$api" --urls "http://127.0.0.1:$1" --Admin:Enabled=true --Db:HistoryIndex="$2" --Plant:DataDir=data/tep-full \
    --Plant:ConfigDir="$tmp/store-$3" --ConnectionStrings:Alarms="Data Source=$tmp/$3.db" --Logging:LogLevel:Default=Warning &
done
for p in 5082 5083; do until curl -sf "http://127.0.0.1:$p/healthz" >/dev/null; do sleep 1; done; done
for p in 5082 5083; do curl -sf -X POST "http://127.0.0.1:$p/api/v1/admin/seed-history?repeat=10"; echo; done
$cli loadtest --url http://127.0.0.1:5082 --concurrency 32 --seconds 20 --label indexed_302k_rows --out "$out"
$cli loadtest --url http://127.0.0.1:5083 --concurrency 32 --seconds 20 --label no_history_index_302k_rows --out "$out"
for p in 5082 5083; do curl -sf -X POST "http://127.0.0.1:$p/api/v1/admin/shutdown" || true; done
rm -rf "$tmp"

# 3. test counts
dotnet test Deadband.sln -c Release --no-build 2>&1 | tee "$tmp.dotnet.txt" || true
(cd web && npx ng test --watch=false) 2>&1 | tee "$tmp.web.txt" || true
python3 - "$out" "$tmp.dotnet.txt" "$tmp.web.txt" <<'PY'
import json, re, sys
out, dn, web = sys.argv[1:]
d = json.load(open(out))
dt = open(dn).read()
passed = sum(int(x) for x in re.findall(r"Passed:\s+(\d+)", dt))
failed = sum(int(x) for x in re.findall(r"Failed:\s+(\d+)", dt))
wt = re.sub(r"\x1b\[[0-9;]*m", "", open(web).read())
m = re.search(r"Tests\s+(?:(\d+) failed \| )?(\d+) passed", wt)
d["tests"] = {
  "dotnetPassed": passed, "dotnetFailed": failed,
  "angularPassed": int(m.group(2)) if m else 0, "angularFailed": int(m.group(1) or 0) if m else None,
}
d["tests"]["totalPassed"] = d["tests"]["dotnetPassed"] + d["tests"]["angularPassed"]
json.dump(d, open(out, "w"), indent=2)
print(d["tests"])
PY
