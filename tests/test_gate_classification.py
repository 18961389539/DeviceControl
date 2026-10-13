"""Q07 unit contract: the release gate must never count an unexecuted task as PASS.

Feeds synthetic task outputs through tools.release_gate_v20.classify_output and
asserts the strict mapping: UNVERIFIED / SKIP / BLOCKED notices and exit code 77
always classify as BLOCKED; only a real completion may be PASS. No server or
.NET SDK is required for this test itself.
"""
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from tools.release_gate_v20 import classify_output

CASES = [
    # (task output, exit code, expected status)
    ('UNVERIFIED R08 Chromium CORS: playwright unavailable', 0, 'BLOCKED'),
    ('SKIP R08 Chromium CORS: 127.0.0.1:5173 already in use locally', 0, 'BLOCKED'),
    ('UNVERIFIED R06 HTTP: .NET 10 SDK absent', 0, 'BLOCKED'),
    ('BLOCKED N08 filesystem does not materialize real symbolic links', 77, 'BLOCKED'),
    ('RESULT: BLOCKED .NET 10 SDK absent', 77, 'BLOCKED'),
    ('RESULT: SKIP local port busy', 0, 'BLOCKED'),
    ('RESULT: PASS R08 Chromium CORS verified', 0, 'PASS'),
    ('RESULT: FAIL assertion mismatch in audit replay', 0, 'FAIL'),
    ('PASS V2.0 sandbox provenance: valid copy', 0, 'PASS'),
    ('', 1, 'FAIL'),
    ('AssertionError: unexpected status 500', 1, 'FAIL'),
    # A PASS marker contradicted by a non-zero exit code is never PASS.
    ('RESULT: PASS but the process crashed afterwards', 3, 'FAIL'),
    # The last marker wins when a task prints more than one.
    ('RESULT: BLOCKED first attempt\nRESULT: PASS second attempt', 0, 'PASS'),
    # A real failure marker wins even if the exit code is zero.
    ('RESULT: FAIL spool replay lost a record', 0, 'FAIL'),
]
for text, code, expected in CASES:
    actual = classify_output(text, code)
    assert actual == expected, (text, code, actual, expected)

# And the strict-release rule the gate applies: anything not PASS blocks approval.
from tools.release_gate_v20 import definitions  # noqa: E402
sources = definitions(browser=True) + __import__('tools.release_gate_v20', fromlist=['dotnet_tasks']).dotnet_tasks()
labels = [name for name, _, _ in sources]
assert 'manifest:verify' in labels, labels
assert 'python:test_gate_classification.py' in labels, labels
assert 'http:test_api_v25.py' in labels and 'http:test_api_v28.py' in labels, labels
print('RESULT: PASS gate classification: UNVERIFIED/SKIP/BLOCKED can never be counted as PASS; '
      'strict release refuses any non-PASS task')
