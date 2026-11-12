#!/usr/bin/env bash
# Download the full Tennessee Eastman (Braatz) data set: 22 training + 22 test files, about 25 MB.
set -euo pipefail
dest="${1:-data/tep-full}"
base="https://raw.githubusercontent.com/camaramm/tennessee-eastman-profBraatz/master"
mkdir -p "$dest"
for i in $(seq -w 0 21); do
  for f in "d${i}.dat" "d${i}_te.dat"; do
    [ -s "$dest/$f" ] || curl -sSfL "$base/$f" -o "$dest/$f"
  done
done
echo "$(ls "$dest" | wc -l | tr -d ' ') files in $dest"
