#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
for version in 06 07 08 09; do
  echo "V0.$version standalone Chromium test"
  python "tests/test_browser_v${version}.py"
done

python tests/test_browser_v10.py
python tests/test_browser_v11.py
python tests/test_browser_v12.py

python tests/test_browser_v13.py
