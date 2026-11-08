#!/usr/bin/env python3
"""Local SpacetimeDB + Prada services. Run: python3 scripts/local-stack.py start."""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time
import tomllib
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / '.spacetime'
CLI = RUNTIME / 'bin/2.14.1/spacetimedb-cli'
RUN = RUNTIME / 'run'
SERVICES = {
    'database': ([str(CLI), '--root-dir', str(RUNTIME), 'start', '--listen-addr', '127.0.0.1:3000', '--non-interactive'], ROOT, 'http://127.0.0.1:3000/v1/ping'),
    'agent': (['node', '--import', 'tsx', 'src/main.ts'], ROOT / 'agent', 'http://127.0.0.1:8788/api/shared'),
    'dashboard': ([str(ROOT / 'robot/.venv/bin/python'), 'dashboard/server.py'], ROOT, 'http://127.0.0.1:8090/api/mission'),
}

def ready(url):
    try:
        with urllib.request.urlopen(url, timeout=1) as r: return r.status == 200
    except OSError: return False

def start(name):
    args, cwd, url = SERVICES[name]
    if ready(url):
        print(name + ': already running')
        return
    RUN.mkdir(parents=True, exist_ok=True)
    with (RUN / (name + '.log')).open('ab') as log:
        process = subprocess.Popen(args, cwd=cwd, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
    (RUN / (name + '.pid')).write_text(str(process.pid))
    for _ in range(40):
        if process.poll() is not None: raise RuntimeError(name + ' exited; see ' + str(RUN / (name + '.log')))
        if ready(url):
            print(name + ': running')
            return
        time.sleep(.25)
    raise RuntimeError(name + ' did not become ready; see ' + str(RUN / (name + '.log')))

def configure():
    token = tomllib.loads((RUNTIME / 'config/cli.toml').read_text()).get('spacetimedb_token')
    if not token: raise RuntimeError('Publish the local database first so its service identity is created.')
    env = ROOT / 'agent/.env'
    text = env.read_text() if env.exists() else ''
    backup = RUNTIME / 'env-before-spacetime'
    if not backup.exists():
        backup.write_text(text); backup.chmod(0o600)
    updates = {'STATE_MODE': 'spacetime', 'SPACETIME_URL': 'http://127.0.0.1:3000', 'SPACETIME_DB': 'rescue-robot', 'SPACETIME_TOKEN': token}
    lines=[]
    for line in text.splitlines():
        key=line.partition('=')[0].strip()
        if key in updates: lines.append(key+'='+updates.pop(key))
        else: lines.append(line)
    lines.extend(key+'='+value for key,value in updates.items())
    env.write_text('\n'.join(lines)+'\n'); env.chmod(0o600)
    print('Configured local SpacetimeDB; service credentials stay in ignored local files.')

def install():
    if CLI.exists(): return
    RUNTIME.mkdir(parents=True,exist_ok=True)
    installer = RUNTIME / 'installer.sh'
    with urllib.request.urlopen('https://install.spacetimedb.com', timeout=30) as r: installer.write_bytes(r.read())
    subprocess.run(['sh',str(installer),'--root-dir',str(RUNTIME),'-y'], env={**os.environ,'SPACETIME_DOWNLOAD_ROOT':'https://github.com/clockworklabs/SpacetimeDB/releases/download/v2.14.1'},check=True)

def publish():
    subprocess.run([str(CLI),'--root-dir',str(RUNTIME),'publish','-s','http://127.0.0.1:3000','-p','server','--yes=skip-login','rescue-robot'],cwd=ROOT,check=True)

def stop():
    for name in ('agent','dashboard','database'):
        path=RUN/(name+'.pid')
        if not path.exists(): continue
        pid=int(path.read_text())
        command=subprocess.run(['ps','-p',str(pid),'-o','command='],capture_output=True,text=True).stdout
        expected={'agent':'src/main.ts','dashboard':'dashboard/server.py','database':'spacetimedb'}[name]
        if expected in command:
            try: os.killpg(pid,signal.SIGINT if name=='dashboard' else signal.SIGTERM)
            except ProcessLookupError: pass
        path.unlink()
        for _ in range(40):
            if not ready(SERVICES[name][2]): break
            time.sleep(.25)
        print(name+': stopped managed process')

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action',choices=['start','database','publish','configure','status','stop'])
    action=parser.parse_args().action
    if action=='stop': stop(); return
    if action=='status':
        for name,(_,_,url) in SERVICES.items(): print(name+': '+('running' if ready(url) else 'unavailable'))
        return
    if action=='configure': configure(); return
    install(); start('database')
    if action=='database': return
    subprocess.run(['npm','ci'],cwd=ROOT/'server',check=True) if not (ROOT/'server/node_modules').exists() else None
    publish()
    if action=='publish': return
    configure()
    subprocess.run(['npm','run','migrate:spacetime'],cwd=ROOT/'agent',check=True)
    start('dashboard'); start('agent')
    print('Dashboard: http://localhost:8090 | Damage: http://localhost:8090/damage')

if __name__=='__main__':
    try: main()
    except (RuntimeError,subprocess.CalledProcessError,OSError) as exc: sys.exit(str(exc))
