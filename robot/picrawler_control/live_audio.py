"""Authenticated PCM16 live radio. Audio devices stay owned by the speech service."""
import asyncio
import hmac
import json
import threading
from websockets.asyncio.server import serve

async def radio(ws, server):
    processes=[];held=False;locked=False
    try:
        hello=json.loads(await asyncio.wait_for(ws.recv(),3))
        if not hmac.compare_digest(str(hello.get('token','')),server.token):
            await ws.close(1008,'Unauthorized');return
        listener=server.listener
        if server.session_mode!='manual' or not listener or not listener.mic:
            raise ValueError('Take responder conversation; robot microphone required')
        locked=listener._busy.acquire(blocking=False)
        if not locked:raise ValueError('Microphone busy')
        server.speaker.stop()
        held=await asyncio.to_thread(server.speaker.hold,2)
        if not held:raise ValueError('Speaker busy')
        generation=server.session_generation
        common=['-q','-t','raw','-f','S16_LE','-r','16000','-c','1']
        record=await asyncio.create_subprocess_exec('arecord','-D',listener.mic,*common,stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.DEVNULL)
        processes.append(record)
        play=await asyncio.create_subprocess_exec('aplay','-D',server.speaker.player.device,*common,stdin=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.DEVNULL)
        processes.append(play)
        await ws.send(json.dumps({'ready':True}))
        async def receive():
            async for chunk in ws:
                if not isinstance(chunk,bytes) or len(chunk)>8192 or len(chunk)%2:raise ValueError('Invalid PCM')
                play.stdin.write(chunk);await asyncio.wait_for(play.stdin.drain(),1)
        async def send():
            while True:
                chunk=await asyncio.wait_for(record.stdout.read(1280),2)
                if not chunk:raise ValueError('Robot microphone stopped')
                await asyncio.wait_for(ws.send(chunk),1)
        async def watch():
            while generation==server.session_generation and server.session_mode=='manual':await asyncio.sleep(.1)
        tasks=[asyncio.create_task(f()) for f in (receive,send,watch)]
        try:
            done,_=await asyncio.wait(tasks,return_when=asyncio.FIRST_COMPLETED)
            for task in done:task.result()
        finally:
            for task in tasks:task.cancel()
            await asyncio.gather(*tasks,return_exceptions=True)
    except Exception as exc:
        try:await ws.send(json.dumps({'error':str(exc)[:160]}))
        except Exception:pass
    finally:
        for process in processes:
            if process.returncode is None:
                process.terminate()
                try:await asyncio.wait_for(process.wait(),2)
                except asyncio.TimeoutError:process.kill();await process.wait()
        if held:server.speaker.release()
        if locked:server.listener._busy.release()
        await ws.close()

def start(server, host, port):
    async def run():
        async with serve(lambda ws:radio(ws,server),host,port,max_size=8192,max_queue=2,compression=None,ping_interval=5,ping_timeout=5):
            await asyncio.Future()
    threading.Thread(target=lambda:asyncio.run(run()),daemon=True,name='live-radio').start()
