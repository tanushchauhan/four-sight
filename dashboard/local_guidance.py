"""Continuous drive from fresh local perception; stale or unknown views hold neutral."""
import json
import math
import threading
import time
import urllib.request

URL='http://127.0.0.1:8790'
MAX_AGE=.75


def fetch():
    with urllib.request.urlopen(URL+'/perception',timeout=.8) as response:
        return json.loads(response.read(64000))


def valid(result, now):
    captured=result.get('captured_at')
    return (result.get('ready') is True and type(captured) in (int,float)
            and math.isfinite(captured) and 0<=now-captured<=MAX_AGE
            and type(result.get('frame_id')) is int
            and result.get('action') in ('forward','left','right','wait'))


class LocalGuide:
    def __init__(self, reader=fetch, clock=time.monotonic, wall=time.time):
        self.reader,self.clock,self.wall=reader,clock,wall
        self.lock=threading.Lock()
        self.reset()

    def reset(self):
        with self.lock:
            self.epoch=object();self.pending=False;self.next_check=0
            self.latest=None;self.received=0;self.candidate=None;self.count=0;self.selected=None
            self.last_frame=None;self.reason='Waiting for local perception'

    def command(self, speed, busy=False):
        with self.lock:
            now=self.clock()
            if not self.pending and now>=self.next_check:
                self.pending=True;self.next_check=now+.1
                threading.Thread(target=self._look,args=(self.epoch,),daemon=True).start()
            result=self.latest
            if not result or not valid(result,self.wall()) or now-self.received>MAX_AGE:
                self.candidate=None;self.count=0;self.selected=None
                self.reason=(result or {}).get('reason','Waiting for fresh local perception')
                if result and result.get('ready'):self.reason='Camera perception stale; holding position'
                return 0,0,self.reason
            action=result['action'];self.reason=result['reason']
            if result['frame_id']!=self.last_frame:
                self.last_frame=result['frame_id']
                self.count=self.count+1 if self.candidate==action else 1
                self.candidate=action
            if action=='wait':
                self.count=0;self.selected=None
                return 0,0,self.reason
            if self.count>=2:
                self.selected=action
            elif self.selected and result.get('sectors',{}).get(self.selected,{}).get('clear') is True:
                # Retain a still-clear route while confirming a different preference.
                action=self.selected
            else:
                return 0,0,'Confirming local route (1/2) — '+self.reason
            power=min(.3,.2+.1*speed)
            forward,turn={'forward':(power,0),'left':(0,power),'right':(0,-power)}[action]
            return forward,turn,self.reason

    def _look(self, epoch):
        try:
            result=self.reader()
            if not isinstance(result,dict):raise ValueError('Invalid local perception')
        except Exception as exc:
            result=dict(ready=False,action='wait',reason='Local perception unavailable ('+type(exc).__name__+'); retrying')
        with self.lock:
            if epoch is self.epoch:
                self.latest=result;self.received=self.clock();self.pending=False
