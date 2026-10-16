// The videos page's list: the entries in videos.json whose MP4 is in public/videos. The Pages workflow downloads the
// files from the release named in videos.json (fetch-videos.sh), so a video is on the site once its file is uploaded.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const site = path.dirname(fileURLToPath(import.meta.url));
const folder = path.join(site, 'public', 'videos');

export default {
  watch: ['videos.json', 'public/videos/*'],
  load() {
    const { videos } = JSON.parse(fs.readFileSync(path.join(site, 'videos.json'), 'utf8'));
    const has = (name) => fs.existsSync(path.join(folder, name));
    return videos
      .filter((video) => has(`${video.file}.mp4`))
      .map((video) => ({
        ...video,
        src: `/videos/${video.file}.mp4`,
        poster: has(`${video.file}.poster.jpg`) ? `/videos/${video.file}.poster.jpg` : null,
        captions: has(`${video.file}.vtt`) ? `/videos/${video.file}.vtt` : null,
      }));
  },
};
