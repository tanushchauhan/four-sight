# Phone door generator

A browser tool that makes the phone door for the 3D-printed CardboardXR viewer, sized for any phone that fits the shell. It's at https://brenpoly.github.io/CardboardXR/door-generator/, served by GitHub Pages.

## What changes and what doesn't
- **Fixed:** the door outline, hinge knuckles with the pin hole, and the hair-tie latch. These mate with the viewer shell, so they never change.
- **Generated from the phone's dimensions:**
  - **Phone pad:** inset 1.5 mm from the phone outline. Its height is set so the screen lands on the shell's screen plane.
  - **TPU corner locators:** they grip the phone's corners with 0.1 mm of squeeze and dovetail into pockets in the door.
  - **Camera opening:** the raised camera area plus clearance.
  - **Vent holes:** they fill the rest of the door.
- **Not included:** the MagSafe steel-ring guide.
- **Thicker phones:** above 8.65 mm, the pad becomes a shallow recess, up to 9.15 mm.
- **Size limits:** phones up to 72.2 × 154 mm. Lens alignment needs no input, because the door centres the phone on the lenses.
- **Corner limits:** the corner locators are in fixed places, so the phone's corners have to fit them:
  - **Short phones:** the locators have to reach round the corners, so the phone must be at least 132.2 mm long with 4 mm corners, or 135.7 mm with 10 mm corners.
  - **Big phones with square corners:** at least 0.5 mm of TPU has to stay round each corner. Under 1 mm, the page warns that the locators will hold less firmly.
- **One piece:** if the door or a locator comes out in more than one piece, the page shows it but won't let you download it.

## Files
| File | What it is |
|---|---|
| `index.html`, `app.js` | Page, form, AI-agent prompt + JSON import, three.js preview |
| `worker.js` | Builds each door in a Web Worker, with a fresh Manifold instance, and writes the 3MF and STL zip |
| `door.js` | The geometry and the size checks, following the Fusion 360 model of the printed doors. Also usable from Node |
| `stl.js` | Binary STL writer |
| `vendor/` | Manifold (CSG, WASM), three.js, fflate. Licences are in `vendor/LICENSES.txt` |

There is no build step. Any static server works, for example `python3 -m http.server`.

## Accuracy
The **iPhone 15 Pro** and **Pixel 9 Pro** presets reproduce the doors modelled in Fusion 360 and printed:
- Every locator is within 0.1 % of the Fusion volume.
- Horizontal slices through the door match the Fusion door to within the vent-hole polygon facets everywhere outside the old MagSafe ring area.
- The locator dovetails are built as 0.2 mm steps, one per print layer, instead of a smooth taper.

## Using it from Node
```js
import Module from './vendor/manifold.js';
import { buildDoor, check } from './door.js';
const spec = { name: 'My phone', length: 146.6, width: 70.6, thickness: 8.25, cornerRadius: 10.2,
  camera: { top: 2.05, left: 2.05, width: 42.5, height: 43.5, radius: 6.5 }, cameraClearance: 1.5 };
const { errors, warnings } = check(spec);   // buildDoor doesn't check the numbers itself
const M = await Module(); M.setup();
const { door, locators } = buildDoor(M, spec);
```

`buildDoor` leaves its intermediate Manifold objects in WebAssembly memory, which JavaScript never frees.
One instance runs out of memory after 50 to 150 doors, so a script that builds many should start a new
`Module()` for each door, as `worker.js` does.
