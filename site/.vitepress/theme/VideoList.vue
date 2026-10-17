<!-- The videos page: every video in site/videos.json whose file the Pages workflow downloaded (site/README.md), each with
     its captions and a transcript of its narration (videos.data.js). -->
<script setup>
import { withBase } from 'vitepress';

defineProps({ videos: { type: Array, required: true } });
</script>

<template>
  <section v-for="video in videos" :id="video.id" :key="video.id" class="video">
    <h2>{{ video.title }}</h2>
    <p>
      {{ video.description }}
      <span v-if="video.length" class="length">Length {{ video.length }}.</span>
    </p>
    <video
      controls
      preload="metadata"
      playsinline
      :aria-label="video.title"
      :class="{ vertical: video.vertical }"
      :poster="video.poster ? withBase(video.poster) : undefined"
      :src="withBase(video.src)"
    >
      <track v-if="video.captions" kind="captions" srclang="en" label="English" :src="withBase(video.captions)" />
    </video>
    <p class="download">
      <a :href="withBase(video.src)" download>Download “{{ video.title }}” (MP4)</a>
      <template v-if="video.captions">
        · <a :href="withBase(video.captions)" download>Captions for “{{ video.title }}” (WebVTT)</a>
      </template>
    </p>
    <details v-if="video.transcript" class="transcript">
      <summary>Transcript</summary>
      <template v-for="(part, i) in video.transcript" :key="i">
        <p>{{ part.text }}</p>
        <p v-for="(text, j) in part.onScreen" :key="j" class="on-screen">On screen: {{ text }}</p>
      </template>
    </details>
  </section>
</template>
