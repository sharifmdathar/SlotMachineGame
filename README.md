# Jackpot 777 — 2D Unity Slot Machine

A data-driven 3-reel 2D slot machine built with **Unity 6000.6.10f1 (URP 2D)** for WebGL. Uses the supplied pixel-art cabinet, symbols, and UI sprite sheets, paired with runtime-synthesized retro audio and a responsive full-viewport WebGL layout.

> **Live WebGL Demo:** https://sharifmdathar.github.io/SlotMachineGame/Build/WebGL/

---

## 1. Game Overview & Architecture Principles

The codebase strictly separates **game rules** from **presentation**:

- **Outcome-first RNG:** Outcomes are drawn once using a cryptographic RNG _before_ any reel starts moving.
- **Presentation-only reels:** Reel controllers animate smoothly toward pre-computed targets, so frame rate or animation timing can never affect payouts.
- **Observer UI:** The HUD listens to controller events and never mutates balance or game state directly.
- **Deterministic scene assembly:** An idempotent Unity Editor builder imports/slices sprite sheets, generates ScriptableObjects and prefabs, builds `MainScene.unity`, and wires all serialized references with zero manual drag-and-drop.

---

## 2. How to Run

### Option A — Play the WebGL Build (Recommended)

A ready-to-run WebGL build is included in `/Build/WebGL` (≈48 MB). Because browsers block WebGL streaming over `file://`, serve it over a local HTTP server:

```bash
cd Build/WebGL
python3 -m http.server 8787
# Open http://localhost:8787/
```

The build uses a custom **`ResponsiveFit`** WebGL template: `#unity-canvas` fills `100vw × 100vh` with zero scrollbars, `CanvasScaler (Expand)` keeps the entire 1920×1080 safe area visible across ultrawide, laptop, and portrait windows, and a 16:9 `EnvelopeParent` backdrop avoids letterbox bars.

### Option B — Run in Unity Editor

1. Open the project in **Unity 6000.6.10f1** (URP 2D).
2. Run **`Tools > Slot Game > Build Complete Project`** (optional—the generated assets and `MainScene.unity` are already committed, and the builder is idempotent).
3. Open **`Assets/Scenes/MainScene.unity`** and press **Play**.

### Rebuild WebGL via CLI

```bash
Unity -batchmode -quit \
  -projectPath "$(pwd)" \
  -executeMethod SlotMachine.EditorTools.SlotGameProjectBuilder.BuildWebGLFromCommandLine \
  -logFile Logs/webgl.log
```

_(WebGL compression is disabled so `/Build/WebGL` runs out-of-the-box on any static HTTP server or GitHub Pages without `.gz`/Brotli header configuration.)_

---

## 3. Controls

| Input                               | Action                                                                                  |
| ----------------------------------- | --------------------------------------------------------------------------------------- |
| **SPIN** / **Spacebar** / **Lever** | Pulls the cabinet lever and starts a paid spin (or consumes a Free Spin when active).   |
| **◀ / ▶ Arrows**                    | Cycles bet size through **10 → 25 → 50 → 100 → 250**.                                   |
| **AUTO OFF / ON**                   | Toggles continuous auto-spin (automatically stops if balance is below the current bet). |
| **PAYTABLE**                        | Opens the payout table modal (reads directly from `SymbolData` assets).                 |
| **YES / NO (Out of Credits)**       | Refills demo credits to the starting balance or dismisses the prompt.                   |

Session balance and bet selection persist locally via `PlayerPrefs` (`SlotMachine.Balance`, `SlotMachine.BetIndex`). Completed spins are also kept in an in-memory five-entry session history: the top-right **RECENT SPINS** card lists the newest first, shows true per-turn net (`payout - committed bet`, or payout only on a free spin), and accumulates a session P&L total. Refilling the demo balance clears the history and session net.

---

## 4. Game Rules & Bonus Features

Only the **center horizontal payline** is evaluated, clearly indicated by an amber center-row band and gold side brackets flanking the reels.

| Symbol     | Weight | 3-of-a-Kind | 2-of-a-Kind (Bonus) | Bonus Behavior                                 |
| ---------- | -----: | ----------: | ------------------: | ---------------------------------------------- |
| **7**      |      1 |     **50×** |                  1× | **Jackpot:** awards 50× bet + **5 Free Spins** |
| **BAR**    |      3 |         20× |                  1× | —                                              |
| **Bell**   |      5 |          8× |                0.5× | —                                              |
| **Cherry** |      7 |          2× |                0.5× | —                                              |

- **Primary Win:** Three identical symbols on the center payline award the symbol's `ThreeOfAKindMultiplier`.
- **Two-Match Bonus:** Two matching symbols on the center payline award a smaller partial payout (`TwoOfAKindMultiplier`) to keep spins engaging; never overrides a triple.
- **Jackpot Free Spins:** Landing triple `7` triggers `WinTier.Jackpot` with fanfare and queues 5 automatic free spins at the triggering bet (retriggerable).
- **Theoretical RTP:** **≈91.6%** under the default weights (including recursive free-spin value).

---

## 5. Project Structure & Architecture

```text
Assets/
├── Editor/
│   └── SlotGameProjectBuilder.cs      # Idempotent importer, slicer, scene/prefab builder & WebGL config
├── Scripts/
│   ├── Core/                          # Pure C# domain logic (no MonoBehaviour)
│   │   ├── IRngProvider.cs
│   │   ├── CryptoRngProvider.cs       # CSPRNG + weighted selection
│   │   ├── SymbolData.cs              # ScriptableObject symbol config
│   │   ├── SpinOutcome.cs             # Immutable spin snapshot (symbols + committed bet)
│   │   ├── SpinHistoryEntry.cs        # Immutable per-turn session history record
│   │   ├── PayoutEvaluator.cs         # Primary, two-match bonus, and jackpot evaluation
│   │   └── GameState.cs               # Idle / Spinning / Stopping / Evaluating / ModalOpen
│   ├── Reels/
│   │   ├── ReelController.cs          # Ring-buffer strip animation, easing & overshoot bounce
│   │   └── SlotMachineController.cs   # State machine, lever trigger, events & PlayerPrefs
│   ├── UI/
│   │   ├── HudController.cs           # Smooth balance count-up, controls, win banner & modals
│   │   ├── SpinHistoryCard.cs         # Top-right RECENT SPINS / session P&L renderer
│   │   ├── PaytableModal.cs
│   │   └── ConfirmResetModal.cs
│   └── Audio/
│       └── ProceduralSfxController.cs # Runtime AudioClip.Create retro synthesis
├── Data/                              # SymbolData assets (7, BAR, Bell, Cherry)
├── Prefabs/                           # SymbolCell & ReelStrip prefabs
├── Scenes/MainScene.unity
├── Sounds/README.md                   # Notes on procedural audio synthesis
├── Animations/README.md               # Notes on curve/coroutine reel & lever animation
├── WebGLTemplates/ResponsiveFit/      # Full-viewport responsive WebGL HTML/CSS template
└── UI/
    ├── Generated/                     # Procedural radial glow & stage plinth textures
    └── Raw/Slot Machine/              # Supplied source sprites
```

### Key Technical Details

- **Fairness & RNG (`CryptoRngProvider`):** Uses `System.Security.Cryptography.RandomNumberGenerator.GetInt32` (rejection-sampled, unbiased, unseeded from `Time` or `UnityEngine.Random`). Weighted selection samples `[0, totalWeight)` once per reel before animation begins.
- **Seamless Reel Animation (`ReelController`):** Uses a fixed ring buffer of pooled `Image` cells inside a `RectMask2D` viewport (zero per-frame allocations), accelerated via an `AnimationCurve` and brought to a staggered left-to-right stop (**1.1s / 1.6s / 2.1s**) with an overshoot-and-settle curve.
- **Procedural Audio (`ProceduralSfxController`):** Synthesizes 6 mono 44.1 kHz clips at startup (`AudioClip.Create`)—button click, lever pull, reel tick, reel-stop thud, win jingle, and jackpot fanfare—using square, triangle, saw, and sine oscillators with short envelopes. _(In WebGL, audio unlocks on the first user click per browser autoplay policy.)_
- **Automated Scene & Sprite Slicing (`SlotGameProjectBuilder`):** Measures and slices the `1×4` button sheets and `2×3` Yes/No sheet via `TextureImporter`, uses Point filtering for pixel art, validates zero null serialized fields (`Tools > Slot Game > Validate Project`), and generates a smoothstep radial glow + feathered plinth so the cabinet integrates cleanly into the background.

---

## 6. Thought Process & Trade-offs

1. **Decoupled Core & Presentation:** Keeping `Core/` pure C# makes RNG and payout evaluation deterministic, auditable, and independent of frame rate or Unity lifecycle timing. `SpinOutcome` captures the committed bet immutably at spin start so rapid UI inputs can't skew payouts.
2. **Idempotent Editor Builder vs. Manual Scene Wiring:** Hand-editing `.unity` scenes doesn't scale well and makes reference regressions easy. Generating the scene, prefabs, sprite slices, and `SerializedObject` bindings in code ensures the whole project is reproducible from raw assets in one click.
3. **9-Slice vs. Simple UI Scaling:** Modal popups use true 9-slice borders (`popup.png`), while compact HUD cards (`BALANCE`, `BET`, `SPIN`) use `Simple` mode so large 90 px sprite borders don't collapse smaller rects.
4. **Procedural Audio & Curve Animation:** Synthesizing SFX in C# and driving reel/lever motion via `AnimationCurve` coroutines keeps the WebGL bundle lean and makes every behavior easy to inspect in code.
5. **Client-Side Persistence Scope:** `PlayerPrefs` is used strictly for lightweight demo credit persistence across reloads; a real-money production machine would evaluate RNG and store balances server-side.
