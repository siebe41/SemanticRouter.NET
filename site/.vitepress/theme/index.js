// The default theme, plus the Mermaid diagrams the README and docs/architecture.md draw and the videos page's player.
import DefaultTheme from 'vitepress/theme';
import Mermaid from './Mermaid.vue';
import VideoList from './VideoList.vue';
import './custom.css';

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('Mermaid', Mermaid);
    app.component('VideoList', VideoList);
  },
};
