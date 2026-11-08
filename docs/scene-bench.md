# Scene test bench

The bench tests the Nemotron agent team on **real images** before any hardware or the real frontend is connected. You pick a scene photo, randomize one, or upload your own. The robot's camera "sees" that image: real pixels go to Nemotron VL. You watch the coordinator, triage and search agents decide live, and the page scores the run against the scene's expected outcome.

```bash
cd agent
npm run web               # http://localhost:8787
npm run eval:scenes       # headless scoreboard over every scene (real Nemotron if agent/.env has a key)
npm run eval:scenes -- --mock     # offline
npm run eval:scenes -- gen-       # only scenes whose id contains "gen-"
```

## How a run works

1. The robot starts 1.8 m from a case, in an open room (`SceneRobot`, `agent/src/scenes/scene-robot.ts`).
2. The on-board detector "fires" with the scene's `detectorConfidence`, which creates a case and a `detection` event for the coordinator.
3. The coordinator delegates to the triage agent. It points the camera, calls `take_snapshot`, and gets the **scene image**. `analyze_image` sends the pixels to `VISION_MODEL`.
4. `speak` / `listen` play the scene's scripted `replies`; `[]` means silence.
5. **Evolving scenes:**
   - **`reposition` frame:** becomes the camera view after the agent moves, turns (10° or more) or pans (10° or more) and then takes a new snapshot. This tests whether the second-look logic actually changes what it sees.
   - **`time` frame:** each one makes the harness advance the clock and send a `recheck_case` event. This tests whether the agents notice the change and escalate.
6. **Scoring** (`scoreRun` in `agent/src/scenes/harness.ts`):
   - Is the final triage the expected one?
   - Did it judge correctly whether a person is present (from what `analyze_image` saw)?
   - Did every `mustDo` tool succeed at least once?
   - Did no `mustNotDo` tool succeed?
7. **Offline mock (`llm: "mock"`):** the rule-based planner runs, and vision comes from the frame captions instead of pixels. This tests the plumbing, not judgment.

## Scenes (`agent/scenes/`)

| File | What it holds |
| --- | --- |
| `manifest-photos.json`, `images/photo-*.jpg`, `ATTRIBUTION.md` | Real, freely licensed photos (mostly search-and-rescue training exercises with role-players), with author and license per image |
| `manifest-generated.json`, `images/gen-*.jpg`, `GENERATED.md` | AI-generated multi-frame **evolving** scenes made with NVIDIA's image models, with prompts recorded |

Any `manifest-*.json` in that folder is loaded. A scene whose image files are missing is skipped with a warning. Images are JPEGs up to 1024 px and under 170 KB, because NVIDIA's inline image limit is about 180 KB. Uploads from the page are resized in the browser to fit.

Scene format (`agent/src/scenes/types.ts`):

```ts
{
  id, title, category, description,
  frames: [{ image: "images/x.jpg", caption: "ground truth, never shown to the agent", trigger: "start" | "reposition" | "time" }],
  detectorConfidence: 0.45,
  replies: ["Yes... I can hear you", "My leg is stuck"],   // [] = silent
  frameReplies?: [null, null, []],                         // optional per-frame override
  expected: { personPresent: true | false | "unclear", triage: "red|yellow|green|black|unknown",
              mustDo: ["speak", "listen", ...], mustNotDo: ["dispatch_responders"], notes },
  source: { kind: "photo" | "generated", url?, author?, license, model?, prompts? }
}
```

## API (`agent/src/web/server.ts`, page in `agent/web/public/`)

| Route | Does |
| --- | --- |
| `GET /api/scenes` | `{scenes, llmAvailable, defaults, models}`; frame images become `/scenes/images/...` URLs |
| `POST /api/runs` | Body `{sceneId?, upload?: {dataUrl, name?, replies?, detectorConfidence?}, agentMode?: "team"\|"single", llm?: "nvidia"\|"mock", approvals?: "auto"\|"ask"}` returns `{runId, scene}` |
| `GET /api/runs/:id/events` | Server-sent events, replayed to late subscribers: `status`, `log` (event, thought, tool, done; with agent and depth), `frame`, `speech`, `confirm`, `state`, `result`, `error` |
| `POST /api/runs/:id/confirm` | `{id, approved}` answers a dispatch or beacon approval when `approvals: "ask"` |

The server binds to `127.0.0.1` only. `WEB_PORT` and `WEB_HOST` override the address. The NVIDIA key is read from `agent/.env` on the server and never sent to the page.
