"""Debounced local YOLO sightings feed the same admission gate as Visor."""
import json
import threading
import time
import urllib.request
from local_guidance import fetch, valid

class PersonWatch:
    def __init__(self, mission, media):
        self.mission,self.media=mission,media
        self.halt=threading.Event();self.worker=threading.Thread(target=self.run,daemon=True)
        self.frame=None;self.hits=0;self.session=None;self.sent=0
    def start(self):self.worker.start()
    def close(self):self.halt.set();self.worker.join(timeout=3)
    def sighting(self, result, state):
        if state['session_id']!=self.session:
            self.session=state['session_id'];self.frame=None;self.hits=0;self.sent=0
        if not state['person_detection'] or state['mode'] not in ('autonomous','watching','person_found') or not valid(result,time.time()):
            self.hits=0;return None
        if result['frame_id']==self.frame:return None
        self.frame=result['frame_id']
        people=[o for o in result.get('objects',[]) if o['label']=='person' and o['confidence']>=.6]
        self.hits=self.hits+1 if people else 0
        if self.hits<3 or time.monotonic()-self.sent<3:return None
        self.sent=time.monotonic()
        survivors=[]
        for person in people:
            x,y,x2,y2=person['box']
            survivors.append(dict(box=[x,y,x2-x,y2-y],confidence=person['confidence'],maskCoverage=0))
        return dict(source='local-yolo',count=len(people),confidence=max(p['confidence'] for p in people),
                    survivors=survivors,box=survivors[0]['box'],frame_time=result['captured_at']*1000)
    def run(self):
        while not self.halt.wait(.2):
            try:
                state=self.mission.snapshot()
                if not state['person_detection'] or state['mode'] not in ('autonomous','watching','person_found'):
                    self.hits=0;continue
                body=self.sighting(fetch(),state)
                if body:
                    request=urllib.request.Request(self.media.agent+'/api/detections',data=json.dumps(body).encode(),headers={'Content-Type':'application/json'})
                    with urllib.request.urlopen(request,timeout=2) as response:response.read(4096)
            except Exception:
                self.hits=0
