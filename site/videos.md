---
title: Videos
description: Demo videos of SemanticRouter.NET, recorded from scripts against the real router.
---

<script setup>
import { data as videos } from './videos.data.js';
</script>

# Videos

Each video is recorded from a script against the real router, its dashboard and Claude Code. Only the models are
stand-ins, so every routing decision, cooldown and figure on screen is the router's own. The captions follow the
narration.

<VideoList :videos="videos" />

<p v-if="videos.length === 0"><em>The videos are on their way.</em> Meanwhile, the <a href="./overview#quick-start-from-source">quick start</a> gets the router running in a few minutes.</p>
