<!-- The videos page: every video in site/videos.json whose file the Pages workflow downloaded (site/README.md). -->
<script setup>
import { withBase } from 'vitepress';

defineProps({ videos: { type: Array, required: true } });
</script>

<template>
  <section v-for="video in videos" :id="video.id" :key="video.id" class="video">
    <h2>{{ video.title }} <span v-if="video.length" class="length">{{ video.length }}</span></h2>
    <p>{{ video.description }}</p>
    <video
      controls
      preload="metadata"
      playsinline
      :class="{ vertical: video.vertical }"
      :poster="video.poster ? withBase(video.poster) : undefined"
      :src="withBase(video.src)"
    >
      <track v-if="video.captions" kind="captions" srclang="en" label="English" :src="withBase(video.captions)" />
    </video>
    <p class="download"><a :href="withBase(video.src)" download>Download the MP4</a></p>
  </section>
</template>
