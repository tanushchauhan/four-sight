# Generated evolving scenes

These images are **AI-generated test fixtures**, not photos of real people or events. They were made for testing the rescue agent's multi-step behaviour on scenes that change between frames. Scenes and expectations are in `manifest-generated.json`.

## How they were made

- Model: `black-forest-labs/flux.1-dev` on the NVIDIA API Catalog (`https://ai.api.nvidia.com/v1/genai/black-forest-labs/flux.1-dev`), `mode: "base"`, 1024x768, 40 steps, cfg_scale 3.5.
- No edit chain. `flux.1-kontext-dev` (image editing) is reachable with this key but only accepts NVIDIA's built-in example images (`Expected: example_id, got: base64` / `got: asset_id`), and flux.1-dev `depth`/`canny` modes reject custom images the same way. So each frame is a separate text-to-image generation. To keep frames of a scene consistent, every frame of a scene uses the **same seed** and nearly the same prompt; only the part describing the change differs. Consistency is therefore approximate: the room and person match closely in most pairs but small details (clothing, furniture position, exact camera angle) can differ.
- Every prompt ends with the shared style suffix shown below. Several seeds were generated per scene and the most consistent, realistic set was kept after looking at each image.
- Converted with `magick in.jpg -resize "1024x1024>" -strip -quality 80 out.jpg` (all files under 170 KB).

## gen-hidden-revealed: Hand under a slab, then the whole person

Evolves: only a hand shows under a fallen slab; a side look reveals a conscious man with his legs under it.

| Frame | Trigger | Seed | What it shows |
| --- | --- | --- | --- |
| `images/gen-hidden-revealed-1.jpg` | start | 111 | Close, low view of a thick fallen concrete slab resting on rubble. Only one bare adult hand and forearm stick out from under its edge; the rest of the body is hidden. No face visible. |
| `images/gen-hidden-revealed-2.jpg` | reposition | 111 | Same kind of slab seen from the side: an adult man in a blue work shirt and jeans lies on his back underneath it, knees bent and lower legs under the slab and rubble, one hand on a rock. Eyes open, mouth open as if in pain or calling out; he is conscious. No blood visible. |

Prompts:

- `gen-hidden-revealed-1` (seed 111): A large fallen concrete ceiling slab leaning diagonally on a pile of concrete rubble; only a single dusty human hand and wrist sticks out from under the edge of the slab, the rest of the person is completely hidden, documentary photograph, shot from a small ground robot camera 30 cm above the floor, low angle, wide lens, earthquake-collapsed building interior, natural dust and debris, realistic lighting, sharp detail, no text
- `gen-hidden-revealed-2` (seed 111): A large fallen concrete ceiling slab leaning diagonally on a pile of concrete rubble; seen from the side: an adult man in a dusty blue work shirt lies on his back, the lower edge of the slab rests on his shins pinning both legs under rubble, he props himself on one elbow, head raised, eyes open, grimacing in pain, looking toward the camera, documentary photograph, shot from a small ground robot camera 30 cm above the floor, low angle, wide lens, earthquake-collapsed building interior, natural dust and debris, realistic lighting, sharp detail, no text

## gen-deteriorating: Trapped man stops responding

Evolves over time: a man propped on his forearms and talking later lies face down, eyes closed, as dust and smoke build up.

| Frame | Trigger | Seed | What it shows |
| --- | --- | --- | --- |
| `images/gen-deteriorating-1.jpg` | start | 233 | Grey-haired man in his 40s in a grey t-shirt lies prone on a dusty concrete floor between rubble and concrete pillars, propped up on his forearms, one hand on a concrete block. Head raised, eyes open, looking toward the camera; alert. Legs stretch out behind him; nothing is visibly pinning them. |
| `images/gen-deteriorating-2.jpg` | time | 233 | Same man and same room later: now lying flat on his front, cheek on his forearm, eyes closed, face slack, not moving. No visible blood. |
| `images/gen-deteriorating-3.jpg` | time | 233 | Same man lying flat face down on the floor, eyes closed, arms limp; grey dust and smoke now hang in the air behind him and the background is hazy. |

Prompts:

- `gen-deteriorating-1` (seed 233): Side view at floor level of an adult man in his 40s with grey hair, grey stubble and a dusty grey t-shirt lying on his stomach on a dusty concrete floor, a thick broken concrete slab lies across the back of his thighs and legs, pinning him down, rubble around; he is propped up on one elbow with his head and chest raised, eyes wide open, looking at the camera, alert, talking, documentary photograph, shot from a small ground robot camera 30 cm above the floor, low angle, wide lens, earthquake-collapsed building interior, natural dust and debris, realistic lighting, sharp detail, no text
- `gen-deteriorating-2` (seed 233): Side view at floor level of an adult man in his 40s with grey hair, grey stubble and a dusty grey t-shirt lying on his stomach on a dusty concrete floor, a thick broken concrete slab lies across the back of his thighs and legs, pinning him down, rubble around; he is unconscious, lying completely flat, his face resting on the floor turned toward the camera, eyes closed, arms limp, motionless, documentary photograph, shot from a small ground robot camera 30 cm above the floor, low angle, wide lens, earthquake-collapsed building interior, natural dust and debris, realistic lighting, sharp detail, no text
- `gen-deteriorating-3` (seed 233): Side view at floor level of an adult man in his 40s with grey hair, grey stubble and a dusty grey t-shirt lying on his stomach on a dusty concrete floor, a thick broken concrete slab lies across the back of his thighs and legs, pinning him down, rubble around; he is unconscious, lying completely flat, his face resting on the floor turned toward the camera, eyes closed, arms limp, motionless; the whole room is now filled with thick swirling grey smoke and falling dust, the background is hidden in smoke, hazy dim light, documentary photograph, shot from a small ground robot camera 30 cm above the floor, low angle, wide lens, earthquake-collapsed building interior, natural dust and debris, realistic lighting, sharp detail, no text

## gen-smoke-rising: Hospital corridor catches fire

Evolves over time: a man sits on a gurney in a damaged corridor; later a doorway is on fire and smoke fills the hall.

| Frame | Trigger | Seed | What it shows |
| --- | --- | --- | --- |
| `images/gen-smoke-rising-1.jpg` | start | 312 | Long hospital corridor with fallen ceiling tiles, hanging cables and rubble along both walls. On the right, mid-distance, a dark-haired man in dark clothes sits on a tilted gurney against the wall, head bowed. Light haze toward the bright far end. No fire. |
| `images/gen-smoke-rising-2.jpg` | time | 312 | Same corridor and same man on the gurney on the right, head bowed, a blanket over his lap. Bright orange flames now burn in an open doorway on the left wall, the light is dimmer, and blue-grey smoke and haze fill the far end of the corridor. |

Prompts:

- `gen-smoke-rising-1` (seed 312): Long collapsed hospital corridor with fallen ceiling tiles, hanging cables, chunks of rubble on the floor and a toppled gurney on the right side; far away at the end of the corridor an adult person sits slumped against the wall, small in frame; light thin dust haze in the air, good visibility, documentary photograph, shot from a small ground robot camera 30 cm above the floor, low angle, wide lens, earthquake-collapsed building interior, natural dust and debris, realistic lighting, sharp detail, no text
- `gen-smoke-rising-2` (seed 312): Long collapsed hospital corridor with fallen ceiling tiles, hanging cables, chunks of rubble on the floor and a toppled gurney on the right side; far away at the end of the corridor an adult person sits slumped against the wall, small in frame; the corridor is filled with thick billowing black and grey smoke that hides the ceiling and the far end, bright orange flames burning out of a doorway on the left wall, very poor visibility, documentary photograph, shot from a small ground robot camera 30 cm above the floor, low angle, wide lens, earthquake-collapsed building interior, natural dust and debris, realistic lighting, sharp detail, no text

## gen-water-rising: Flooded basement, water rising

Evolves over time: a woman waits on a workbench in a flooded basement; later the water is up near the tabletop.

| Frame | Trigger | Seed | What it shows |
| --- | --- | --- | --- |
| `images/gen-water-rising-1.jpg` | start | 421 | Basement with cracked concrete walls and one fluorescent tube light, floor flooded with murky brown water. A woman in an olive jacket and jeans sits on the edge of a wooden workbench in the middle of the room, feet hanging just above the water, looking at the camera. The table legs show well above the water. |
| `images/gen-water-rising-2.jpg` | time | 421 | Same kind of basement and workbench: the water is clearly higher, only short stubs of the table legs show above it, and there are ripples. The woman now crouches on top of the tabletop with her feet pulled up, looking down at the water. |

Prompts:

- `gen-water-rising-1` (seed 421): Basement storage room flooded with murky brown water, cracked concrete walls, a fluorescent tube light, the camera is at the water surface; in the middle of the room a sturdy wooden workbench with an adult woman in an olive green jacket and jeans on top of it; the water is shallow, ankle deep, the tall legs of the workbench stand clearly above the water, she sits on the edge of the workbench with dry feet, looking at the camera, documentary photograph, shot from a small ground robot camera 30 cm above the floor, low angle, wide lens, earthquake-collapsed building interior, natural dust and debris, realistic lighting, sharp detail, no text
- `gen-water-rising-2` (seed 421): Basement storage room flooded with murky brown water, cracked concrete walls, a fluorescent tube light, the camera is at the water surface; in the middle of the room a sturdy wooden workbench with an adult woman in an olive green jacket and jeans on top of it; deep flood water, waist deep, the water is so high that the workbench is completely underwater except its top surface which sits flush with the water, she crouches on it hugging her knees, frightened, ripples around her, documentary photograph, shot from a small ground robot camera 30 cm above the floor, low angle, wide lens, earthquake-collapsed building interior, natural dust and debris, realistic lighting, sharp detail, no text

## gen-false-alarm: Dark shape is a jacket and a backpack

Evolves on a second look: a dark heap that looks like a person from a distance is a puffer jacket on a backpack up close.

| Frame | Trigger | Seed | What it shows |
| --- | --- | --- | --- |
| `images/gen-false-alarm-1.jpg` | start | 512 | Low view down a rubble-strewn office aisle between rows of desks. In the foreground lies a dark navy rounded heap with sleeve-like shapes that could be a person curled up face down; a smaller dark lump lies further back. No face or skin visible. |
| `images/gen-false-alarm-2.jpg` | reposition | 511 | Close-up of a dark navy puffer jacket lying on top of a black backpack (straps visible) in concrete rubble, office chairs and desks behind. Clearly empty clothing and a bag; no person. |

Prompts:

- `gen-false-alarm-1` (seed 512): Collapsed office floor with concrete rubble and broken desks, a dark navy puffer jacket crumpled on top of a black backpack lying in the rubble; seen from far away across the room in dim light, small in the middle distance, the dark crumpled shape resembles a person lying curled up in the rubble, documentary photograph, shot from a small ground robot camera 30 cm above the floor, low angle, wide lens, earthquake-collapsed building interior, natural dust and debris, realistic lighting, sharp detail, no text
- `gen-false-alarm-2` (seed 511): Collapsed office floor with concrete rubble and broken desks, a dark navy puffer jacket crumpled on top of a black backpack lying in the rubble; close-up from one meter away, clearly just an empty jacket and a backpack, no person, documentary photograph, shot from a small ground robot camera 30 cm above the floor, low angle, wide lens, earthquake-collapsed building interior, natural dust and debris, realistic lighting, sharp detail, no text

## gen-second-person: Panning reveals a second survivor

Evolves on a pan: a woman sits by a sofa; turning the camera shows a man lying on the floor further back.

| Frame | Trigger | Seed | What it shows |
| --- | --- | --- | --- |
| `images/gen-second-person-1.jpg` | start | 612 | Damaged living room with plaster rubble across the floor. A barefoot woman with long brown hair, denim jacket and jeans sits on the floor leaning against a grey sofa, head tilted, eyes half open, looking at the camera. Conscious; no visible wounds. |
| `images/gen-second-person-2.jpg` | reposition | 612 | Wider view of a similar room: the same-looking woman (denim jacket, barefoot) sits on the floor in front of the grey sofa on the left; further back near a doorway a second adult, a man in a dark sweater and grey trousers, lies on his side on the floor among plaster rubble, head up, looking toward the camera. |

Prompts:

- `gen-second-person-1` (seed 612): Collapsed apartment living room with an overturned grey sofa and fallen plaster; an adult woman in her 30s wearing a dusty denim jacket sits on the floor leaning against the overturned sofa, conscious, looking at the camera, holding her left forearm, dust on her face, documentary photograph, shot from a small ground robot camera 30 cm above the floor, low angle, wide lens, earthquake-collapsed building interior, natural dust and debris, realistic lighting, sharp detail, no text
- `gen-second-person-2` (seed 612): Collapsed apartment living room with a grey sofa and fallen plaster; wider view of the room: on the left an adult woman with long brown hair, a dusty denim jacket, jeans and bare feet sits on the floor in front of the grey sofa, and further back in the room near a doorway a second adult, a man in a dark sweater, lies on his side on the floor among plaster rubble, partly covered in dust, documentary photograph, shot from a small ground robot camera 30 cm above the floor, low angle, wide lens, earthquake-collapsed building interior, natural dust and debris, realistic lighting, sharp detail, no text

## Known inconsistencies

- gen-hidden-revealed: frame 2 is a fresh generation from a side angle, so the slab and rubble are similar but not pixel-identical to frame 1.
- gen-deteriorating: the man wears khaki trousers in frame 1 and grey ones in frames 2-3; no slab is visibly on his legs in any frame (only a block by his hand in frame 1). The smoke in frame 3 is moderate, mostly in the background.
- gen-smoke-rising: the person is mid-distance on a gurney (not far away against a wall), and in frame 2 he has a blanket over his lap. Smoke is moderate haze rather than a black wall.
- gen-water-rising: the room differs slightly between frames (wall cracks); the rise is shown by the table legs going from long to short stubs, not by water at the tabletop.
- gen-false-alarm: frame 2 was generated with a different seed (511) than frame 1 (512), because it showed the jacket and backpack most clearly; the office background differs a little.
- gen-second-person: the room layout (doorways, sofa) is close but not identical between the two frames.
