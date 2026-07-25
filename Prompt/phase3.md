Phase 3 — Bubbles + seaweed

Restated facts: pool bubbles (no per-frame alloc). Sim is Core/neutral.

Build (Core):

    BubbleSystem: floor (and chest) emitters; spawn rate scales with BubbleDensity. Each bubble pos, radius, riseSpeed, wobblePhase. Per frame: y -= riseSpeed*delta*Speed; x += sin(t*wf+phase)*amp*delta; recycle when above surface. Object pool, fixed capacity.
    Seaweed: strands rooted on sand; N segments; horizontal offset amp*sin(t*freq+phase+seg*k) with amplitude growing toward tip; per-strand phase/freq/height/x from seed.
    Renderer draws bubble sprite (rim highlight) and tapered kelp.

Fedora tests: pool count stable after warm-up (no growth over 10k ticks); bubble x stays within wobble amplitude; bubble recycles exactly once past surface; seaweed offsets bounded; determinism per seed.

