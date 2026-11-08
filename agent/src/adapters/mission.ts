/** Dashboard owns motion. Detection must stop exploration before a case is opened. */
export class MissionLink {
  private timer: NodeJS.Timeout | null = null;
  constructor(private url: string, private token: string, private fetchImpl = fetch) {}
  private async post(path: string, body: unknown): Promise<Record<string, unknown>> {
    const r = await this.fetchImpl(`${this.url.replace(/\/$/, '')}/api/agent/${path}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${this.token}` },
      body: JSON.stringify(body), signal: AbortSignal.timeout(1500),
    });
    if (!r.ok) throw new Error(`Mission dashboard HTTP ${r.status}`);
    return await r.json() as Record<string, unknown>;
  }
  async admit(sighting: unknown): Promise<boolean> {
    try {
      const result = await this.post('person', sighting);
      if (result.accepted === true && sighting && typeof sighting === 'object')
        (sighting as { sessionId?: unknown }).sessionId = result.session_id;
      return result.accepted === true;
    }
    catch { return false; }
  }
  start(): void {
    const pulse = () => void this.post('heartbeat', {}).catch(() => {});
    pulse();
    this.timer = setInterval(pulse, 1000);
    this.timer.unref();
  }
  stop(): void { if (this.timer) clearInterval(this.timer); }
}
