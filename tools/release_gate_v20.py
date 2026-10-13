#!/usr/bin/env python3
"""DeviceControl V2.0 software RELEASE GATE.

Release succeeds ONLY if offline regressions, Chromium smoke, .NET 10 build,
contract runner and real HTTP tests all return exit code 0. Tests are bounded,
independently logged and machine-readable. Without dotnet: no release approval.
Does not connect to industrial hardware.

Q07 status contract: a task's status comes from `classify_output` - an explicit
`RESULT: PASS|FAIL|BLOCKED|SKIP` marker plus the exit code. Exit code 0 is NEVER
sufficient by itself: an unexecuted task printing UNVERIFIED/SKIP, or carrying
exit code 77 (BLOCKED), is refused for release instead of being counted as PASS.
"""
import argparse
import datetime as dt
import hashlib
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PY = sys.executable
NODE = shutil.which('node') or 'node'

# Q07: unified structured-status contract. A task may print `RESULT: PASS|FAIL|BLOCKED|SKIP`
# as its final verdict line. Statuses are derived from the marker AND the exit code;
# an unexecuted task (UNVERIFIED / SKIP / BLOCKED) can never be counted as PASS, and a
# strict release is refused while any task is not PASS.
RESULT_MARKER = re.compile(r'^RESULT:\s*(PASS|FAIL|BLOCKED|SKIP)\b(.*)$', re.MULTILINE)
UNEXECUTED_HINT = re.compile(r'\b(UNVERIFIED|SKIP)\b|\bBLOCKED\b')

def classify_output(text, returncode):
    """Map a task's output plus exit code to PASS / FAIL / BLOCKED.

    - exit code 77 is the project-wide BLOCKED convention;
    - an explicit `RESULT:` marker wins (a PASS marker contradicted by a non-zero
      exit code is treated as FAIL, never PASS);
    - a zero exit code with an UNVERIFIED/SKIP/BLOCKED notice is BLOCKED, not PASS;
    - everything else falls back to the exit code."""
    text = text or ''
    if returncode == 77:
        return 'BLOCKED'
    marker = None
    for candidate in RESULT_MARKER.finditer(text):
        marker = candidate  # last marker wins
    if marker is not None:
        status = 'BLOCKED' if marker.group(1) in ('BLOCKED', 'SKIP') else marker.group(1)
        if status == 'PASS' and returncode != 0:
            return 'FAIL'
        return status
    if returncode != 0:
        return 'FAIL'
    if UNEXECUTED_HINT.search(text):
        return 'BLOCKED'
    return 'PASS'

def _terminate_tree(proc):
    """F11: a timeout must take the whole process tree down. `dotnet run` spawns a
    child app; killing only the direct child leaves orphaned listeners behind."""
    try:
        if os.name == 'nt':
            subprocess.run(['taskkill', '/PID', str(proc.pid), '/T', '/F'],
                           capture_output=True, timeout=20)
        else:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        try: proc.kill()
        except Exception: pass

def write_progress(out, profile, results, complete=False):
    partial = {'version': '2.0-rc', 'complete': complete, 'profile': profile,
               'tests': results, 'passed': sum(x['status'] == 'PASS' for x in results)}
    (out / 'progress.json').write_text(json.dumps(partial, ensure_ascii=False, indent=2), encoding='utf-8')

def definitions(browser=True):
    js = [
        'test_mock.mjs', 'test_governance.mjs', 'test_workflow_v05.mjs',
        'test_workflow_v06.mjs', 'test_workflow_v07.mjs', 'test_workflow_v08.mjs',
        'test_replay_v10.mjs', 'test_station_v11.mjs',
        'test_resources_v12.mjs', 'test_workspace_v13.mjs',
        'test_templates_v14.mjs', 'test_upgrade_rules_v15.mjs',
        'test_recovery_v18.mjs', 'test_api_client_v20.mjs', 'test_api_client_v21.mjs'
    ]
    py = [
        'test_recovery_sandbox_v19.py', 'test_recovery_v20.py',
        'test_recovery_v19_static.py', 'test_recovery_v18_static.py',
        'test_rollout_files_v16.py', 'test_rollout_v17_static.py',
        'test_station_files_v12.py', 'test_template_files_v14.py',
        'test_upgrade_files_v15.py', 'test_sqlite_schema.py',
        'test_sqlite_migration_v08.py', 'test_sqlite_reliability.py',
        'test_sqlite_migration_v09.py', 'test_sqlite_v10.py',
        'test_v20_recovery_contract.py'
    ]
    tasks = [('offline:preview-build', [PY, 'tools/build_preview.py'], 60),
             ('manifest:verify', [PY, 'tools/release_manifest.py', '--verify'], 60),
             ('python:test_gate_classification.py', [PY, 'tests/test_gate_classification.py'], 30)]
    tasks += [('node:' + name, [NODE, 'tests/' + name], 50) for name in js]
    tasks += [('python:' + name, [PY, 'tests/' + name], 50) for name in py]
    if browser:
        tasks += [('browser:' + name, [PY, 'tests/' + name], 90) for name in (
            'test_browser_v19.py', 'test_browser_v18.py',
            'test_browser_v17.py', 'test_browser_v16.py', 'test_browser_v21.py')]
    return tasks

def dotnet_tasks():
    project = 'backend/DeviceControl.Api/DeviceControl.Api.csproj'
    ct = 'backend/DeviceControl.ContractTests/DeviceControl.ContractTests.csproj'
    return [
        ('dotnet:build-api', ['dotnet', 'build', project, '-c', 'Release', '--nologo'], 180),
        ('dotnet:build-contracts', ['dotnet', 'build', ct, '-c', 'Release', '--nologo'], 180),
        ('dotnet:contract-runner', ['dotnet', 'run', '--no-build', '-c', 'Release', '--project', ct], 90),
        *[('http:' + test, [PY, 'tests/' + test], 150) for test in (
            'test_api_v09.py', 'test_api_v10.py', 'test_api_v12.py',
            'test_api_v13.py', 'test_api_v14.py', 'test_api_v15.py',
            'test_api_v16.py', 'test_api_v17.py', 'test_api_v18.py',
            'test_api_v19.py', 'test_api_v21.py', 'test_api_v22.py', 'test_api_v23.py',
            'test_api_v24.py', 'test_api_v25.py', 'test_api_v26.py',
            'test_api_v27.py', 'test_api_v28.py')]
    ]

def execute(label, args, timeout, directory):
    started = time.monotonic()
    proc = subprocess.Popen(args, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            text=True, errors='replace',
                            env={**os.environ, 'PYTHONUNBUFFERED': '1'},
                            start_new_session=(os.name != 'nt'))
    try:
        out, err = proc.communicate(timeout=timeout)
        rc = proc.returncode
        out = (out or '') + ('\n[stderr]\n' + err if err else '')
        # Q07: the exit code alone never decides; structured output decides.
        status = classify_output(out, proc.returncode)
    except subprocess.TimeoutExpired:
        status, rc = 'TIMEOUT', None
        _terminate_tree(proc)
        try:
            out, err = proc.communicate(timeout=15)
        except Exception:
            out, err = '', ''
        out = (out or '') + ('\n[stderr]\n' + (err or '') if err else '') + \
              f'\n[TIMEOUT {timeout}s; process tree terminated]'
    elapsed = round(time.monotonic() - started, 2)
    safe = ''.join(ch if ch.isalnum() or ch in '-_.' else '_' for ch in label)
    log = directory / (safe + '.log')
    log.write_text(out[-180000:], encoding='utf-8')
    print(f'{status:8} {elapsed:6.2f}s {label}', flush=True)
    return {'name': label, 'status': status, 'exitCode': rc, 'seconds': elapsed,
            'log': str(log.relative_to(ROOT)), 'command': args}

def main():
    ap = argparse.ArgumentParser(description='Strict V2.0 release gate with per-case evidence')
    ap.add_argument('--profile', choices=['offline','release'], default='release')
    ap.add_argument('--no-browser', action='store_true', help='offline diagnostics only; NOT release eligible')
    ap.add_argument('--only', type=str, default='', help='comma-separated case names for targeted OFFLINE diagnostics')
    ap.add_argument('--output', type=Path, default=ROOT/'artifacts/v20-gate')
    args = ap.parse_args()
    if args.only and args.profile == 'release':
        ap.error('--only is forbidden for a release: all gates are mandatory')
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    logs = out/'logs'; logs.mkdir(exist_ok=True)
    started = dt.datetime.now(dt.timezone.utc)
    results = []
    suite = definitions(not args.no_browser)
    if args.only:
        needles = [x.strip() for x in args.only.split(',') if x.strip()]
        suite = [case for case in suite if any(x in case[0] for x in needles)]
        if not suite: ap.error('--only did not select any cases')
    for label, command, timeout in suite:
        results.append(execute(label, command, timeout, logs))
        # F11: persist partial evidence after EVERY case, offline and dotnet phases alike.
        write_progress(out, args.profile, results)
    sdk = shutil.which('dotnet')
    if sdk:
        # Offline profile is for local triage only; it does not run dotnet, even when present.
        if args.profile == 'release':
            for label, command, timeout in dotnet_tasks():
                results.append(execute(label, command, timeout, logs))
                write_progress(out, args.profile, results)
    else:
        if args.profile == 'release':
            for label, command, _ in dotnet_tasks():
                results.append({'name': label, 'status': 'BLOCKED_NO_SDK',
                                'exitCode': None, 'seconds': 0, 'log': None, 'command': command})
    passed = sum(x['status']=='PASS' for x in results)
    failed = [x for x in results if x['status'] in ('FAIL','ERROR','TIMEOUT')]
    blocked = [x for x in results if x['status'].startswith('BLOCKED')]
    complete = (args.profile=='release' and not args.no_browser and not failed
                and not blocked and len(results)==len(suite)+len(dotnet_tasks()))
    root_files = [ROOT/'backend/DeviceControl.Api/Program.cs',
                  ROOT/'backend/DeviceControl.Api/StationUpgradeManager.cs',
                  ROOT/'tools/recovery_sandbox.py']
    hashes = {str(x.relative_to(ROOT)): hashlib.sha256(x.read_bytes()).hexdigest() for x in root_files}
    report = {
        'product': 'DeviceControl', 'version': '2.0-rc',
        'startedUtc': started.isoformat(), 'finishedUtc': dt.datetime.now(dt.timezone.utc).isoformat(),
        'profile': args.profile, 'browserIncluded': not args.no_browser,
        'dotnetPresent': bool(sdk), 'dotnetLocation': sdk,
        'passed': passed, 'failed': len(failed), 'blocked': len(blocked),
        'releaseApproved': complete, 'tests': results, 'sourceSha256': hashes,
        'limitations': [] if complete else ['Release not approved: offline-only, failing, missing, or unverified gates']
    }
    (out/'release-gate.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    (out/'progress.json').unlink(missing_ok=True)
    text = ['DeviceControl V2.0 software quality gate',
            f'Profile: {args.profile}; browser: {not args.no_browser}; .NET SDK found: {bool(sdk)}',
            f'Passed: {passed}, failed: {len(failed)}, blocked: {len(blocked)}',
            f'RELEASE APPROVED: {"YES" if complete else "NO"}', '',
            *[f'{x["status"]:16} {x["name"]}' for x in results], '',
            'This is a software-only simulation. Not hardware / industrial safety qualification.']
    (out/'release-gate.txt').write_text('\n'.join(text)+'\n', encoding='utf-8')
    print('\n'.join(text[:4]), flush=True)
    if args.profile == 'release':
        return 0 if complete else 2
    return 1 if failed else 0

if __name__=='__main__': sys.exit(main())
