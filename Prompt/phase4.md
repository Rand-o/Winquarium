Phase 4 — Fish

Restated facts: depth-sorted draw; procedural sprites; edge flip faces travel direction.

Build (Core):

    Fish: species, depth∈[0,1]→scale 0.5–1.2 + far-fish opacity/blue-tint, baseSpeed, dir(±x), bobAmp, bobFreq, bobPhase. Update: x += dir*speed*Speed*delta; y = baseY + bobAmp*sin(t*bobFreq+phase); small random chance/sec to change depth or dart. Off far edge → respawn opposite edge, new y/depth/speed, flipX matches dir.
    FishFactory: builds FishCount fish from seed.
    Depth-sort fish each frame; procedural fish sprite (ellipse body + triangle tail) in AssetStore; PNG override if files exist.

Fedora tests: crossing far edge respawns on opposite side with flipped dir and matching flipX; depth→scale/opacity stays in range; depth sort orders correctly; same seed reproduces fish stream, different seeds diverge.

