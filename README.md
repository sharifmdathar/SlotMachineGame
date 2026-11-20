# Slot Machine — 2D Unity Technical Assignment

A complete, data-driven 3-reel slot machine built in **Unity 6000.6.10f1 (URP 2D)** for WebGL. The project uses the supplied pixel-art cabinet, reel symbols, popup, banner, and button sheets, with **zero external audio files** — every sound effect is synthesized at runtime.

The assignment deliberately separates **pure game rules** from **presentation**:

- The RNG chooses the outcome **once**, before any reel starts moving.
- Reel controllers only animate toward a pre-computed target, so animation timing can never change the payout.
- The HUD observes controller events and never mutates balance or game state directly.
- All serialized references in `MainScene` are assigned by the editor builder; there are no manual drag-and-drop steps.

---

## 1. Run It

### Option A — Unity Editor (recommended for inspection)

1. Open the project with **Unity 6000.6.10f1** (URP 2D template).
2. Run **`Tools > Slot Game > Build Complete Project`** once. The builder imports/textures/slices the raw art, creates the four `SymbolData` assets, creates both prefabs, rebuilds `Assets/Scenes/MainScene.unity`, wires every `[SerializeField]`, and registers the scene in Build Settings.
3. Open **`Assets/Scenes/MainScene.unity`** and press **Play**.

The builder is **idempotent**: it can be run again at any time and will rebuild generated content from the untouched raw files under `Assets/UI/Raw/Slot Machine/`.

### Option B — Play the WebGL build

A successful build is already in `Build/WebGL/` (≈48 MB):

```bash
cd Build/WebGL
python3 -m http.server 8787
# Open http://localhost:8787/
```

Use a local HTTP server; opening `index.html` through `file://` will not work because the WebGL loader fetches streaming files.

The build uses the project-local **ResponsiveFit** template. The canvas fills the browser viewport with no letterbox or pillarbox bars and no scrollbars; a 16:9 `EnvelopeParent` background guarantees full coverage while `CanvasScaler.Expand` keeps all HUD, reels, and modals inside the visible area on ultrawide, laptop, and portrait windows.

### Rebuild WebGL from the command line

```bash
Unity -batchmode -quit \
  -projectPath "$(pwd)" \
  -executeMethod SlotMachine.EditorTools.SlotGameProjectBuilder.BuildWebGLFromCommandLine \
  -logFile Logs/webgl.log
```

The menu equivalent is **`Tools > Slot Game > Build WebGL`**. Compression is intentionally disabled in Player Settings, so the output can be served by any static file server without `.gz`/Brotli configuration. If you enable compression later, also enable decompression fallback or configure the server's `Content-Encoding` headers.

---

## 2. Controls

| Input | Action |
|---|---|
| **SPIN** button | Starts a paid spin (or a free spin when the free-spin badge is active). |
| **Spacebar** | Same as SPIN while the machine is idle. |
| **Lever** (right side of cabinet) | Clickable. The lever animates down/up and starts a spin. |
| **◀ / ▶ bet arrows** | Cycles the bet through **10 → 25 → 50 → 100 → 250**. |
| **AUTO OFF / AUTO ON** | Toggles the automatic spin loop. Auto-spin stops itself if the balance cannot cover the bet. |
| **PAYTABLE** | Opens the paytable modal. The close X uses the sliced close-button sheet. |
| **YES / NO in “OUT OF CREDITS”** | Refills the demo balance to the starting amount, or dismisses the dialog. |

The balance and selected bet are persisted with `PlayerPrefs` (`SlotMachine.Balance`, `SlotMachine.BetIndex`) so a page reload keeps session progress. **PlayerPrefs is client-side demo persistence, not a secure wallet.**

---

## 3. Game Rules

Only the **center payline** is evaluated. The center row is marked in-game by a faint amber band behind the reels and gold brackets at the outer edges of the three reel windows; a thin edge line frames the active row without covering the symbols.

| Symbol | Weight | Triple match | Two match | Notes |
|---|---:|---:|---:|---|
| **7** | 1 | **50×** | 1× | Triple 7 is the **Jackpot**: pays 50× and awards **5 free spins**. |
| **BAR** | 3 | 20× | 1× | |
| **Bell** | 5 | 8× | 0.5× | |
| **Cherry** | 7 | 2× | 0.5× | |

- **Primary rule:** three identical symbols pay the symbol's `ThreeOfAKindMultiplier`.
- **Bonus rule:** exactly two identical symbols pay the symbol's `TwoOfAKindMultiplier`. This is a deliberately small, frequent award that keeps the reels lively; it never overrides a triple.
- **Jackpot:** triple `7` upgrades the result to `WinTier.Jackpot`, plays the fanfare, and queues five free spins at the triggering bet. Free spins play automatically and can themselves retrigger the jackpot.
- **Theoretical RTP** with the default weights: **≈91.6%** (including the recursive value of jackpot free spins). This is a demo machine, not a gambling product.

The paytable modal reads the same `SymbolData` assets used by the RNG and evaluator, so displayed values cannot drift from gameplay.

---

## 4. Architecture

```
Assets/
├── Editor/
│   └── SlotGameProjectBuilder.cs      # idempotent import, slice, asset, prefab, scene + build pipeline
├── Scripts/
│   ├── Core/                          # pure C# — no MonoBehaviour
│   │   ├── IRngProvider.cs
│   │   ├── CryptoRngProvider.cs       # CSPRNG + weighted selection
│   │   ├── SymbolData.cs              # ScriptableObject authoring data
│   │   ├── SpinOutcome.cs             # immutable 3-symbol + bet result
│   │   ├── PayoutEvaluator.cs         # primary, bonus, and jackpot rules
│   │   └── GameState.cs               # Idle / Spinning / Stopping / Evaluating / ModalOpen
│   ├── Reels/
│   │   ├── ReelController.cs          # seamless ring-buffer reel, easing, overshoot/settle
│   │   └── SlotMachineController.cs   # session owner, events, bets, PlayerPrefs, free spins
│   ├── UI/
│   │   ├── HudController.cs           # balance count-up, bet, spin/auto, banners, modals
│   │   ├── PaytableModal.cs
│   │   └── ConfirmResetModal.cs
│   └── Audio/
│       └── ProceduralSfxController.cs # AudioClip.Create synthesis
├── Data/                              # generated SymbolData assets: 7, BAR, Bell, Cherry
├── Prefabs/                           # generated SymbolCell + ReelStrip prefabs
├── Scenes/MainScene.unity             # generated and fully wired
├── Sounds/README.md                   # explains the generated-audio approach
├── Animations/README.md               # explains the curve/coroutine animation approach
├── WebGLTemplates/ResponsiveFit/      # project-local full-viewport WebGL template
└── UI/Raw/Slot Machine/               # supplied raw art, never renamed or modified
```

### 4.1 RNG and fairness (`Core`)

`CryptoRngProvider` uses `System.Security.Cryptography.RandomNumberGenerator.GetInt32`:

- `GetInt32` uses the OS CSPRNG and rejection sampling, so there is no modulo bias.
- Weighted selection sums the `SymbolData.Weight` values and draws one uniform integer in `[0, totalWeight)`. Walking cumulative weights selects each symbol with probability `weight / totalWeight`.
- The RNG is stateless; it is never seeded from time or `UnityEngine.Random`. Results are therefore independent of frame rate and of the previous spin.
- `GenerateSpinIndices` performs one independent weighted draw per reel. All three indices are generated before a single reel starts, so the presentation layer cannot influence the result.

`PayoutEvaluator` is pure C# and takes an immutable `SpinOutcome` (three symbols + the bet that was active at spin start). Free spins and mid-session bet changes therefore cannot alter an already-paid outcome.

### 4.2 Reel math (`Reels`)

`ReelController` implements an infinite strip inside a `RectMask2D` viewport with a small ring of reusable `Image` cells:

- `_offset` is measured in **symbol units**. An integer offset centers one symbol; the fractional part is the distance scrolled into the next symbol.
- Visible slot `k` is positioned at `(k - frac) * cellHeight` and displays `(floor(offset) + k) mod symbolCount`. Cells leaving the viewport are recycled by the modulo, so play has no per-frame allocations.
- Acceleration is an `AnimationCurve` used as a **speed multiplier**; the reel ramps into a fast loop.
- Stopping uses a second curve that rises past `1.0` and settles back to `1.0`. Driving the offset with it creates a visible **overshoot and settle-back**.
- `StartSpin(targetIndex, stopTime)` targets at least two full laps beyond the current offset, so the final position is deterministic and the overshoot never appears to land “in front” of the last visible symbol.
- The controller staggers the three stop times at **1.1 s / 1.6 s / 2.1 s**, producing the classic left-to-right stop cadence.

### 4.3 Session flow (`SlotMachineController`)

```
Idle ──RequestSpin──▶ Spinning ──first reel stops──▶ Stopping
  ▲                                                    │
  │                                                    ▼
  └──────────── Evaluating ◀── all reels stopped ──────┘
                                   │
                                   └── payout + free spins + hold ──▶ Idle
Idle ──OpenModal──▶ ModalOpen ──CloseModal──▶ Idle
```

On spin start the controller:

1. Validates state, balance, and free-spin availability.
2. Commits the bet (or consumes one free spin).
3. Calls the RNG **before** the first reel moves.
4. Fires `OnSpinStarted`, plays the lever SFX, and animates the lever.
5. Starts the three reels with their staggered stop times.
6. Evaluates the immutable outcome, adds payout, awards free spins, highlights winning reels, and fires `OnSpinCompleted`.
7. Hands control to auto-spin/free-spin scheduling or returns to idle.

Public events: `OnSpinStarted`, `OnReelStopped`, `OnSpinCompleted`, `OnBalanceChanged`, `OnBetChanged`, `OnFreeSpinsChanged`, `OnAutoSpinChanged`, `OnInsufficientFunds`, `OnStateChanged`.

### 4.4 UI and audio

`HudController` is a pure observer:

- Balance count-up is a coroutine with smoothstep easing; it stops and restarts cleanly when a new balance event arrives.
- Spin/bet/auto/paytable buttons are refreshed from controller state every frame, so input is disabled during spins and modals.
- The win banner uses a `CanvasGroup` fade/scale; jackpot results use a gold treatment.
- The free-spin badge shows remaining spins; the spin label changes to `FREE SPIN`.
- `PaytableModal` and `ConfirmResetModal` are separate components with their own open/close events. The HUD owns the modal state transition on the controller.

`ProceduralSfxController` synthesizes six mono 44.1 kHz clips at startup with `AudioClip.Create`: button click, lever pull, reel tick, reel-stop thud, win jingle, and jackpot fanfare. The synthesis uses square/triangle/saw/sine waves with short attack/decay envelopes, keeps peaks below full scale, and requires no imported audio. Browser autoplay rules apply to WebGL: audio starts after the first user gesture.

---

## 5. Editor Builder

`Assets/Editor/SlotGameProjectBuilder.cs` is the single source of truth for generated content.

### Asset mapping

| Raw file | Role |
|---|---|
| `bg_gradient.png` | 1920×1080 background, stretched to the canvas. |
| `slot-machine4.png` | Cabinet frame with exact transparent reel cutouts. |
| `slot-machine5.png` | Cyan reel-window backing strips. |
| `slot-machine2.png` / `slot-machine3.png` | Lever-up / lever-down overlays. |
| `slot-symbol1..4.png` | Symbols `7`, `Cherry`, `Bell`, `BAR` (96×96 pixel art). |
| `popup.png` | 9-sliced modal panel (90 px border). |
| `slot_machine_Middle_box.png` | Win banner and SPIN button background (42 px border, used Simple for the small button). |
| `slot_machine_buttons-04.png` | 1×4 left-arrow sheet: Normal / Hover / Pressed / Disabled. |
| `slot_machine_buttons-03.png` | 1×4 right-arrow sheet. |
| `slot_machine_buttons-02.png` | 1×4 close-X sheet. |
| `Yes_No_Btn.png` | 2×3 sheet: YES left column, NO right column; three states each. |

### Importer and slice settings

- Pixel-art symbols, cabinet, backing, and lever overlays use **Point** filtering and uncompressed textures.
- The background and panel/banner art use **Bilinear** filtering.
- Button sheets use `TextureImporterType.Sprite`, `SpriteImportMode.Multiple`, and explicit pixel rects measured from the source art. The obsolete `TextureImporter.spritesheet` API is wrapped in `#pragma warning disable CS0618`.
- 9-slice borders are authored at 90 px (`popup.png`) and 42 px (`slot_machine_Middle_box.png`). Small HUD panels intentionally use `Image.Type.Simple` because a 90 px border would swallow a 320×130 HUD rect; the large modals use true sliced rendering.

### Scene construction

The scene is built entirely through the Unity API — **no hand-written `.unity` YAML**. Layer order:

```
Background
  MachineRoot
    CabinetGlow        (generated soft-alpha radial glow, lowest layer)
    MachinePlinth      (dark-indigo pedestal with a baked gold lip)
    ReelWindowBacking
    Reels
      ReelViewport_1 (RectMask2D)
        PaylineBand (behind symbols)
        ReelStrip → SymbolCells…
      ReelViewport_2
      ReelViewport_3
    MachineCabinetFrame
    PaylineBrackets (gold side markers over the cabinet frame)
    LeverOverlay → LeverHitArea (clickable)
  HUD
  PaytableModal
  ConfirmResetModal
```

The machine fades into the halftone backdrop instead of ending on a hard horizontal cut:

- **`CabinetGlow`** is a `Texture2D` generated procedurally at build time (256×256 RGBA, written to `Assets/UI/Generated/PurpleRadialGlow.asset`) and rendered through a `RawImage` at 1.45× the cabinet size. Its alpha uses a smoothstep falloff (`a = f²(3−2f)`, `f = 1 − r`), which is tangent-flat at the center and **exactly 0 at the texture edge**, so there is no visible rim where the glow meets the background.
- **`MachinePlinth`** is a code-generated `StageBase.asset` (128×96) with an indigo body, a lit ledge, and a gold lip baked into the top 12% of the texture. The texture is feathered on all four sides and the plinth is tucked 8 px behind the cabinet's bottom edge, so the lip surfaces as one continuous soft line under the machine rather than peeking past the cabinet ends.

The plinth and glow are children of `MachineRoot`, so they inherit its 0.94 scale and 16 px lift with the cabinet and reels.

The Canvas uses **Scale With Screen Size**, reference resolution **1920×1080**, and `ScreenMatchMode.Expand`, so the reference canvas grows on non-16:9 screens instead of cropping. Corner HUD panels use a **48 px safe-area inset** on the reference canvas; the cabinet group is scaled to 0.94 and lifted 16 px; reel symbols are inset to 0.88 of their cell height; and the SPIN/SPACE stack is vertically centered with a smaller label. The background is a 16:9 envelope that always covers the viewport. Every generated MonoBehaviour’s serialized fields are assigned through `SerializedObject`; `ValidateSceneReferences()` scans builder-owned components for null object references and reports the count.

`Tools > Slot Game > Validate Project` re-opens the scene and runs the same reference audit. A successful build logs:

```
[SlotGame] MainScene registered in Build Settings (GUID …).
[SlotGame] Build complete. MainScene is wired and added to Build Settings.
```

### WebGL Player Settings

- `runInBackground = true`
- Default web screen size **1920×1080** (reference resolution; CSS stretches the canvas to the viewport and Unity resizes the render target to match)
- Custom template **`PROJECT:ResponsiveFit`**
- WebGL compression **Disabled** (static-server friendly)
- Exception support **None** (smaller, faster release build)
- Application identifier `com.hiring.slotmachine`
- `decompressionFallback` is set through reflection only on Unity versions where the property still exists, so the builder stays warning-free across versions.

### Responsive WebGL template

`Assets/WebGLTemplates/ResponsiveFit/` is a project-local Unity WebGL template, so the responsive behavior survives every rebuild:

- `html, body` are full-viewport, `overflow: hidden`, dark `#0d081d`, and use flex centering.
- `#unity-container` uses `width: 100vw` and `height: 100vh` (with a `100dvh` refinement for mobile browser chrome), so no letterbox or pillarbox area exists.
- `#unity-canvas` fills the container at `width: 100%; height: 100%`; Unity’s `matchWebGLToCanvasSize` keeps the render target in sync.
- The in-canvas `Background` uses a 16:9 `AspectRatioFitter` in `EnvelopeParent` mode, so the art always covers the viewport even when `CanvasScaler.Expand` makes the canvas wider or taller than 16:9.
- The footer is removed; a compact loading overlay and a fullscreen button are the only chrome.
- The builder sets `PlayerSettings.WebGL.template = "PROJECT:ResponsiveFit"` in `ConfigureBuildSettings()`.

---

## 6. Verification Performed

- **Compile:** batch-mode project build completed with exit code 0 and no C# errors or warnings.
- **Scene wiring:** `ValidateSceneReferences()` reported zero missing serialized references.
- **Hierarchy:** verified via the Unity CLI scene hierarchy: camera + URP data, `EventSystem` with `InputSystemUIInputModule`, canvas, three masked reel viewports, cabinet, lever, HUD, and both modals are present.
- **Runtime:** entered play mode and verified:
  - idle preview symbols load from `SymbolData`;
  - spin deducts the bet and transitions `Idle → Spinning → Stopping → Evaluating → Idle`;
  - payouts are added, the win/loss banner appears, and `NO WIN · TRY AGAIN` displays correctly;
  - auto-spin toggles and continues spins;
  - paytable and out-of-credits modals render with the sliced art and close correctly.
- **WebGL:** build succeeded at **≈48.1 MB**; served with `python3 -m http.server`, the loader booted in a browser and the game rendered.
- **Responsive fit:** verified at 1402×876, 805×964, and 427×925 viewports. The canvas and container exactly matched the viewport in every case, `scrollWidth`/`scrollHeight` matched `innerWidth`/`innerHeight`, the background covered the full frame, and no footer or scrollbars appeared. The Unity Game view at 1280×720 confirmed that the scaled cabinet, centered reels, 2-line paytable footer, and HUD label spacing render correctly.
- **Payline:** verified in both the Unity Game view and the WebGL browser preview that the amber center-row band, edge lines, and gold side brackets render without covering symbol art.

---

## 7. Thought Process & Trade-offs

1. **Inspect before coding.** I measured every raw PNG (dimensions, transparent bounds, sheet layout) and read the existing project version and package manifest. The cabinet’s window cutouts were measured in source pixels, then mapped into the 1920×1080 canvas so the reel masks align exactly with the art instead of being eyeballed.
2. **Keep rules pure.** `Core` has no `MonoBehaviour` and no `UnityEngine.Random`. The outcome is immutable and generated before animation. This makes the game testable, auditable, and immune to frame-rate-dependent payout bugs.
3. **Make reels presentation-only.** `ReelController` receives a target index and a stop time. It never decides what symbol wins. The ring-buffer layout avoids instantiating/destroying cells every frame and makes overshoot/settle easy to reason about.
4. **Let the builder be the contract.** Hand-wiring dozens of serialized references in a scene is fragile. The builder creates the scene, assigns every reference through `SerializedObject`, then validates for nulls. Re-running it is safe and keeps generated assets in sync with the source art.
5. **Slice the supplied sheets once, at import time.** The arrow/close sheets are 1×4 state columns and the Yes/No sheet is 2 columns × 3 rows. Slicing them in the importer lets the UI use real `SpriteSwap` transitions instead of tint approximations.
6. **Synthesize audio instead of shipping files.** The assignment asks for six effects; generating them with `AudioClip.Create` keeps the WebGL payload small and makes the sound design self-contained and inspectable in C#.
7. **Design for a 9-slice pitfall.** The first visual pass revealed that a 90 px 9-slice border collapses small HUD panels into dark blobs. The fix was deliberate: large modals keep true slicing; small HUD panels use `Simple`, while the art still provides the border look at their size.
8. **Demo-safe persistence.** `PlayerPrefs` is appropriate for a client-side demo. A production machine would keep balance and outcomes server-side; the README states this explicitly rather than implying the client RNG is a secure wallet.

---

## 8. Suggested Commit Plan

A clean history for this assignment:

1. `chore: initialize Unity 2D project and preserve supplied art`
2. `feat(core): add CSPRNG, weighted symbol data, immutable outcome, and payout evaluator`
3. `feat(reels): implement seamless reel animation, staggered stops, and session controller`
4. `feat(ui): add HUD, paytable, confirmation modal, and procedural SFX`
5. `feat(editor): add idempotent asset importer, prefab/scene builder, and WebGL settings`
6. `docs: add README with architecture, controls, RNG design, and build instructions`

---

## 9. Known Limitations

- This is a **demo**, not a gambling product. RNG and balance are client-side; there is no server authority, anti-cheat, or real-money handling.
- `PlayerPrefs` is local to the browser profile and can be cleared by the user.
- WebGL audio requires a user gesture before the browser will allow playback.
- The project uses a 1920×1080 reference layout. `CanvasScaler.Expand` keeps the fixed-size HUD and cabinet safe at other aspect ratios, while the 16:9 background envelope fills any extra viewport area with art rather than black bars.
