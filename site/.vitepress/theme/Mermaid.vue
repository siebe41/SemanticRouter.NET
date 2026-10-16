<!-- A ```mermaid block, drawn in the browser in the site's light or dark colours (config.mjs turns the block into this). -->
<script setup>
import { onMounted, ref, watch } from 'vue';
import { useData } from 'vitepress';

const props = defineProps({ code: { type: String, required: true } });
const { isDark } = useData();
const svg = ref('');
let count = 0;

async function draw() {
  const { default: mermaid } = await import('mermaid');
  mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: isDark.value ? 'dark' : 'default' });
  const id = `mermaid-${Math.random().toString(36).slice(2)}-${count++}`;
  svg.value = (await mermaid.render(id, decodeURIComponent(props.code))).svg;
}

onMounted(draw);
watch(isDark, draw);
</script>

<template>
  <div class="mermaid-diagram" v-html="svg" />
</template>
