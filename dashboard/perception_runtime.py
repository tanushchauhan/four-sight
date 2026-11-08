"""Start the optional local model process with the dashboard, without importing ML."""
import json
from pathlib import Path
import subprocess
import urllib.request

class PerceptionRuntime:
    def __init__(self):
        self.process=None
        self.log=None
    def start(self):
        try:
            with urllib.request.urlopen('http://127.0.0.1:8790/perception',timeout=.5) as response:
                if isinstance(json.loads(response.read(64000)),dict):return
        except Exception:pass
        root=Path(__file__).resolve().parent
        python=root/'.venv-vision/bin/python'
        if not python.exists():
            print('Local vision dependencies not installed; autonomous motion will hold.',flush=True)
            return
        cache=root/'.vision-cache';cache.mkdir(exist_ok=True)
        self.log=(cache/'service.log').open('a')
        self.process=subprocess.Popen([str(python),str(root/'perception.py')],cwd=root,
            stdin=subprocess.DEVNULL,stdout=self.log,stderr=subprocess.STDOUT)
    def close(self):
        if self.process is not None:
            self.process.terminate()
            try:self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:self.process.kill();self.process.wait()
        if self.log:self.log.close()
