import { createHash } from 'node:crypto';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { SpacetimeState } from '../adapters/spacetime-state.js';
import type { OperatorVoice, Utterance } from '../operator/voice.js';
import type { Config } from '../config.js';

/** Shared records live in Spacetime; live hardware leases remain in the local controller. */
export class SharedService {
  private timer?: NodeJS.Timeout;
  private stopped = false;
  private voiceQueue = Promise.resolve();
  private lastError: string | null = null;
  constructor(readonly db: SpacetimeState, private dashboard: string) {}
  static from(config: Config) {
    return config.stateMode === 'spacetime'
      ? new SharedService(new SpacetimeState(config.spacetimeUrl,config.spacetimeDb,config.spacetimeToken,config.searchArea),process.env.DASHBOARD_URL ?? 'http://127.0.0.1:8090') : null;
  }
  async initialize(voice: OperatorVoice | null) {
    await this.db.sql('SELECT id FROM robot'); // Fail startup explicitly if persistence is unavailable.
    if (voice) {
      const rows=(await this.db.sql('SELECT id, value_json FROM operator_update')).sort((a,b)=>Number(a.id)-Number(b.id)).slice(-50);
      voice.restore(rows.map(r=>JSON.parse(String(r.value_json)) as Utterance));
      voice.subscribe(u=>{
        this.voiceQueue=this.voiceQueue.then(()=>this.db.call('append_operator_update',[JSON.stringify(u)]))
          .catch(e=>{this.lastError=(e as Error).message;console.error('[spacetime] operator update was not saved:',this.lastError);});
      });
    }
    const pulse=async()=>{
      try {
        const r=await fetch(this.dashboard.replace(/\/$/,'')+'/api/status',{signal:AbortSignal.timeout(2000)});
        if(!r.ok)throw new Error('Dashboard unavailable for shared snapshot');
        const snapshot=await r.json();
        await this.db.saveSharedState('dashboard',{...snapshot,savedAt:Date.now()});this.lastError=null;
      }catch(e){this.lastError=(e as Error).message;}
      if(!this.stopped){this.timer=setTimeout(pulse,2000);this.timer.unref();}
    };
    void pulse();
  }
  async audit(record: Record<string,unknown>) {
    const value=JSON.stringify(record);
    await this.db.call('import_app_event',[createHash('sha256').update(value).digest('hex'),value]);
  }
  async routes(req:IncomingMessage,res:ServerResponse,url:URL):Promise<boolean> {
    if(url.pathname!=='/api/shared')return false;
    if(req.method!=='GET'){res.writeHead(405);res.end();return true;}
    try {
      const [dashboard,robots,cases,requests,alerts,hazards,updates,incidents]=await Promise.all([
        this.db.getSharedState('dashboard'),this.db.getRobots(),this.db.getCases(),this.db.getTakeoverRequests(),
        this.db.getAlerts(),this.db.getHazards(),this.db.sql('SELECT id, value_json FROM operator_update'),
        this.db.sql('SELECT id FROM damage_incident'),
      ]);
      res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store'});
      res.end(JSON.stringify({storage:'spacetime',connected:true,error:this.lastError,dashboard,robots,cases,requests,alerts,hazards,
        operatorUpdates:updates.sort((a,b)=>Number(b.id)-Number(a.id)).slice(0,20).map(r=>JSON.parse(String(r.value_json))),incidentCount:incidents.length}));
    }catch(e){res.writeHead(503,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(JSON.stringify({storage:'spacetime',connected:false,error:(e as Error).message}));}
    return true;
  }
  stop(){this.stopped=true;clearTimeout(this.timer);}
}
