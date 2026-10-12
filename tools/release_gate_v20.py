#!/usr/bin/env python3
"""DeviceControl V2.0 software RELEASE GATE.

Release succeeds ONLY if offline regressions, Chromium smoke, .NET 10 build,
contract runner and real HTTP tests all return exit code 0. Tests are bounded,
independently logged and machine-readable. Without dotnet: no release approval.
Does not connect to industrial hardware.
"""
import argparse
import datetime as dt
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PY = sys.executable
NODE = shutil.which('node') or 'node'

def definitions(browser=True):
    js = [
        'test_mock.mjs', 'test_governance.mjs', 'test_workflow_v05.mjs',
        'test_workflow_v06.mjs', 'test_workflow_v07.mjs', 'test_workflow_v08.mjs',
        'test_replay_v10.mjs', 'test_station_v11.mjs',
        'test_resources_v12.mjs', 'test_workspace_v13.mjs',
        'test_templates_v14.mjs', 'test_upgrade_rules_v15.mjs',
        'test_recovery_v18.mjs'
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
    tasks = [('offline:preview-build', [PY, 'tools/build_preview.py'], 60)]
    tasks += [('node:' + name, [NODE, 'tests/' + name], 50) for name in js]
    tasks += [('python:' + name, [PY, 'tests/' + name], 50) for name in py]
    if browser:
        tasks += [('browser:' + name, [PY, 'tests/' + name], 90) for name in (
            'test_browser_v19.py', 'test_browser_v18.py',
            'test_browser_v17.py', 'test_browser_v16.py')]
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
            'test_api_v19.py')]
    ]

def execute(label, args, timeout, directory):
    started = time.monotonic()
    try:
        result = subprocess.run(args, cwd=ROOT, capture_output=True, text=True,
                                errors='replace', timeout=timeout,
                                env={**os.environ, 'PYTHONUNBUFFERED': '1'})
        status = 'PASS' if result.returncode == 0 else 'FAIL'
        out = (result.stdout or '') + ('\n[stderr]\n' + result.stderr if result.stderr else '')
        rc = result.returncode
    except subprocess.TimeoutExpired as e:
        status, rc = 'TIMEOUT', None
        def decoded(x):
            if x is None: return ''
            return x.decode(errors='replace') if isinstance(x, bytes) else x
        out = decoded(e.stdout) + '\n[stderr]\n' + decoded(e.stderr) + f'\n[TIMEOUT {timeout}s]'
    except OSError as e:
        status, rc, out = 'ERROR', None, str(e)
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
        # Persist partial evidence even if an outer runner times out or is killed.
        partial = {'version':'2.0-rc','complete':False, 'profile':args.profile,
                   'tests':results, 'passed':sum(x['status']=='PASS' for x in results)}
        (out/'progress.json').write_text(json.dumps(partial,ensure_ascii=False,indent=2),encoding='utf-8')
    sdk = shutil.which('dotnet')
    if sdk:
        # Offline profile is for local triage only; it does not run dotnet, even when present.
        if args.profile == 'release':
            for label, command, timeout in dotnet_tasks():
                results.append(execute(label, command, timeout, logs))
    else:
        if args.profile == 'release':
            for label, command, _ in dotnet_tasks():
                results.append({'name': label, 'status': 'BLOCKED_NO_SDK',
                                'exitCode': None, 'seconds': 0, 'log': None, 'command': command})
    passed = sum(x['status']=='PASS' for x in results)
    failed = [x for x in results if x['status'] in ('FAIL','ERROR','TIMEOUT')]
    blocked = [x for x in results if x['status']=='BLOCKED_NO_SDK']
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
