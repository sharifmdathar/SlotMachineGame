# Motion

Reel acceleration, overshoot, and settle-back are driven by `AnimationCurve` fields on `ReelController`. The lever pull and UI banners are coroutine-driven so the animation timing can be tuned in one place without external .anim assets.
