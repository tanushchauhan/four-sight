"""Mac-local YOLO + SegFormer perception server; never sends motor commands."""
import argparse
import io
import json
import os
from pathlib import Path
import threading
import time
import urllib.request
from urllib.parse import urlsplit, parse_qs
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT=Path(__file__).resolve().parent
CACHE=ROOT/'.vision-cache'
os.environ.setdefault('HF_HOME',str(CACHE/'huggingface'))
os.environ.setdefault('YOLO_CONFIG_DIR',str(CACHE/'ultralytics'))
os.environ.setdefault('HF_HUB_DISABLE_TELEMETRY','1')

class Models:
    def __init__(self):
        import torch
        from transformers import AutoImageProcessor, SegformerForSemanticSegmentation
        from ultralytics import YOLO
        self.torch=torch
        self.device='mps' if torch.backends.mps.is_available() else 'cpu'
        torch.set_num_threads(4)
        CACHE.mkdir(parents=True,exist_ok=True)
        model='nvidia/segformer-b5-finetuned-ade-640-640'
        self.processor=AutoImageProcessor.from_pretrained(model, use_fast=False)
        self.segmenter=SegformerForSemanticSegmentation.from_pretrained(model).eval().to(self.device)
        self.floor_ids=[int(k) for k,v in self.segmenter.config.id2label.items() if v.lower() in ('floor','rug')]
        if not self.floor_ids:raise ValueError('Segmentation model has no floor label')
        self.detector=YOLO(str(CACHE/'yolo26n.pt'))

    def analyze(self, jpeg):
        import numpy as np
        import cv2
        from PIL import Image, ImageDraw
        from local_navigation import evaluate, PATHS
        image=Image.open(io.BytesIO(jpeg)).convert('RGB')
        image.thumbnail((768,432))
        started=time.monotonic()
        with self.torch.inference_mode():
            inputs=self.processor(images=image,return_tensors='pt').to(self.device)
            logits=self.segmenter(**inputs).logits
            logits=self.torch.nn.functional.interpolate(logits,size=(108,192),mode='bilinear',align_corners=False)
            probs=logits.softmax(dim=1)
            confidence,classes=probs.max(dim=1)
            floor=((classes[...,None]==self.torch.tensor(self.floor_ids,device=self.device)).any(-1)&(confidence>=.55))[0].cpu().numpy()
            result=self.detector.predict(image,device=self.device,imgsz=416,conf=.35,verbose=False)[0]
        objects=[]
        for box,score,label in zip(result.boxes.xyxyn.cpu().tolist(),result.boxes.conf.cpu().tolist(),result.boxes.cls.cpu().tolist()):
            objects.append(dict(box=box,confidence=round(score,3),label=result.names[int(label)]))
        # Erode floor to give boundaries a small visual margin.
        floor=cv2.erode(floor.astype(np.uint8),np.ones((3,3),np.uint8)).astype(bool)
        decision,usable=evaluate(floor,objects)
        overlay=np.asarray(image).copy()
        mask=cv2.resize(usable.astype(np.uint8),image.size,interpolation=cv2.INTER_NEAREST).astype(bool)
        overlay[mask]=(overlay[mask]*.65+np.array([35,200,90])*.35).astype(np.uint8)
        preview=Image.fromarray(overlay);draw=ImageDraw.Draw(preview)
        for name,polygon in PATHS.items():
            points=[(int(x*image.width),int(y*image.height)) for x,y in polygon]
            color='#67ff9a' if decision['sectors'][name]['clear'] else '#ffae57'
            draw.line(points+[points[0]],fill=color,width=2)
        for obj in objects:
            x1,y1,x2,y2=obj['box'];rect=(x1*image.width,y1*image.height,x2*image.width,y2*image.height)
            draw.rectangle(rect,outline='#ff6868',width=2);draw.text((rect[0],rect[1]),obj['label'],fill='#ff6868')
        draw.rectangle((0,0,image.width,24),fill='#14251d')
        draw.text((8,6),decision['action'].upper()+' | local floor + object detection',fill='white')
        output=io.BytesIO();preview.save(output,format='JPEG',quality=80)
        return dict(decision,objects=objects,inference_ms=round((time.monotonic()-started)*1000),device=self.device,
                    model='YOLO26n + NVIDIA SegFormer B5',width=image.width,height=image.height),output.getvalue()

class Perception:
    def __init__(self,camera):
        self.camera=camera.rstrip('/')
        self.condition=threading.Condition()
        self.result=dict(action='wait',reason='Loading local vision models',ready=False)
        self.jpeg=None;self.sampled=0;self.sequence=0
        self.halt=threading.Event()
    def run(self):
        try:models=Models()
        except Exception as exc:
            with self.condition:self.result=dict(action='wait',reason='Model loading failed: '+str(exc)[:200],ready=False);self.condition.notify_all()
            return
        while not self.halt.is_set():
            started=time.monotonic()
            try:
                sampled_wall=time.time()
                with urllib.request.urlopen(self.camera+'/frame',timeout=2) as response:
                    age=float(response.headers.get('X-Frame-Age','inf'))
                    jpeg=response.read(2_000_001)
                if not 0<=age<.5 or len(jpeg)>2_000_000:raise ValueError('Camera frame stale or oversized')
                sampled=started-age
                result,preview=models.analyze(jpeg)
                elapsed=time.monotonic()-sampled
                if elapsed>.75:raise ValueError('Local vision warming up or too slow')
                with self.condition:
                    self.sequence+=1;self.sampled=sampled
                    self.result=dict(result,ready=True,frame_id=self.sequence,captured_at=sampled_wall-age)
                    self.jpeg=preview;self.condition.notify_all()
            except Exception as exc:
                with self.condition:
                    self.result=dict(action='wait',reason='Local vision unavailable: '+str(exc)[:180],ready=False)
                    self.jpeg=None;self.condition.notify_all()
                self.halt.wait(.5)
            self.halt.wait(max(0,.15-(time.monotonic()-started)))
    def snapshot(self,after=0,wait=0):
        with self.condition:
            if wait and self.result.get('captured_at',0)<=after:
                self.condition.wait_for(lambda:self.result.get('captured_at',0)>after or not self.result.get('ready'),timeout=wait)
            result=dict(self.result)
            age=time.monotonic()-self.sampled
            result['age_ms']=round(age*1000) if self.sampled else None
            if result.get('ready') and (age>.75 or result.get('captured_at',0)<=after):
                result.update(action='wait',reason='Waiting for fresh local perception',ready=False)
            return result

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        parsed=urlsplit(self.path)
        if parsed.path=='/perception':
            try:after=float(parse_qs(parsed.query).get('after',['0'])[0])
            except ValueError:after=0
            body=json.dumps(self.server.perception.snapshot(after,1 if after else 0)).encode();kind='application/json';code=200
        elif parsed.path=='/frame':
            with self.server.perception.condition:
                body=self.server.perception.jpeg if self.server.perception.snapshot().get('ready') else None
            code=200 if body else 503;body=body or b'Perception unavailable';kind='image/jpeg' if code==200 else 'text/plain'
        else:code=404;body=b'Not found';kind='text/plain'
        self.send_response(code);self.send_header('Content-Type',kind);self.send_header('Content-Length',str(len(body)));self.send_header('Cache-Control','no-store');self.end_headers()
        try:self.wfile.write(body)
        except (BrokenPipeError,ConnectionResetError):pass
    def log_message(self,*_):pass

def main():
    from mission import settings
    parser=argparse.ArgumentParser();parser.add_argument('--camera');parser.add_argument('--port',type=int,default=8790)
    args=parser.parse_args();state=Perception(args.camera or settings().get('CAMERA_URL','http://prada.local:8080'))
    server=ThreadingHTTPServer(('127.0.0.1',args.port),Handler);server.perception=state
    threading.Thread(target=state.run,daemon=True).start()
    print(f'Local perception: http://127.0.0.1:{args.port}',flush=True)
    try:server.serve_forever()
    except KeyboardInterrupt:pass
    finally:state.halt.set();server.server_close()

if __name__=='__main__':main()
