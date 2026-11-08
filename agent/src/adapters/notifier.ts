export interface NotifyResult {
  to: string;
  ok: boolean;
  id?: string;
  error?: string;
}

/** Sends a text to responders. */
export interface Notifier {
  send(to: string[], body: string): Promise<NotifyResult[]>;
}

/** Prints instead of sending. Default for offline runs. */
export class ConsoleNotifier implements Notifier {
  readonly sent: { to: string[]; body: string }[] = [];
  constructor(private readonly print: (msg: string) => void = console.log) {}

  async send(to: string[], body: string): Promise<NotifyResult[]> {
    const targets = to.length ? to : ["(no RESPONDER_PHONES set)"];
    this.sent.push({ to: targets, body });
    this.print(`\n📟 SMS → ${targets.join(", ")}\n${body}\n`);
    return targets.map((t) => ({ to: t, ok: true, id: "console" }));
  }
}

/** Real SMS through Twilio's REST API (no SDK). */
export class TwilioNotifier implements Notifier {
  constructor(
    private readonly accountSid: string,
    private readonly authToken: string,
    private readonly from: string,
    private readonly fetchImpl: typeof fetch = fetch,
  ) {}

  async send(to: string[], body: string): Promise<NotifyResult[]> {
    if (to.length === 0) return [{ to: "", ok: false, error: "no RESPONDER_PHONES configured" }];
    const url = `https://api.twilio.com/2010-04-01/Accounts/${this.accountSid}/Messages.json`;
    const auth = Buffer.from(`${this.accountSid}:${this.authToken}`).toString("base64");
    return Promise.all(
      to.map(async (number) => {
        try {
          const res = await this.fetchImpl(url, {
            method: "POST",
            headers: { Authorization: `Basic ${auth}`, "Content-Type": "application/x-www-form-urlencoded" },
            body: new URLSearchParams({ To: number, From: this.from, Body: body.slice(0, 1500) }),
          });
          const json = (await res.json()) as { sid?: string; message?: string };
          return res.ok ? { to: number, ok: true, id: json.sid } : { to: number, ok: false, error: json.message ?? `HTTP ${res.status}` };
        } catch (e) {
          return { to: number, ok: false, error: (e as Error).message };
        }
      }),
    );
  }
}
