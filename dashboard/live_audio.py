"""Local-only browser radio proxy; robot credentials never reach the browser."""
import asyncio
import hmac
import json
import threading
from urllib.parse import urlsplit
from websockets.asyncio.client import connect
from websockets.asyncio.server import serve

async def relay(browser, server):
    try:
        hello=json.loads(await asyncio.wait_for(browser.recv(),3))
        if not hmac.compare_digest(str(hello.get('nonce','')),server.nonce) or server.mission.mode!='manual':
            await browser.close(1008,'Take responder conversation first');return
        host=urlsplit(server.media.speech).hostname
        address='['+host+']' if ':' in host else host
        async with connect('ws://'+address+':8082',proxy=None,max_size=8192,max_queue=2,compression=None,open_timeout=4) as robot:
            await robot.send(json.dumps({'token':server.media.token}))
            async def copy(source,destination):
                async for message in source:await asyncio.wait_for(destination.send(message),1)
            async def watch():
                while server.mission.mode=='manual':await asyncio.sleep(.1)
            tasks=[asyncio.create_task(copy(browser,robot)),asyncio.create_task(copy(robot,browser)),asyncio.create_task(watch())]
            try:
                done,_=await asyncio.wait(tasks,return_when=asyncio.FIRST_COMPLETED)
                for task in done:task.result()
            finally:
                for task in tasks:task.cancel()
                await asyncio.gather(*tasks,return_exceptions=True)
    except Exception as exc:
        try:await browser.send(json.dumps({'error':'Live audio disconnected: '+type(exc).__name__}))
        except Exception:pass
    finally:await browser.close()

def start(server):
    async def run():
        async with serve(lambda ws:relay(ws,server),'127.0.0.1',8091,origins=['http://localhost:8090','http://127.0.0.1:8090'],max_size=8192,max_queue=2,compression=None,ping_interval=5,ping_timeout=5):await asyncio.Future()
    threading.Thread(target=lambda:asyncio.run(run()),daemon=True).start()
