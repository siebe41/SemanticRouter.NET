// The videos page's list: the entries in videos.json whose MP4 is in public/videos. The Pages workflow downloads the
// files from the release named in videos.json (fetch-videos.sh), so a video is on the site once its file is uploaded.
//
// Each video needs captions (<file>.vtt, or an .srt that fetch-videos.sh turns into one) unless videos.json marks it
// "speech": false: a video whose file is here without them fails the build, so captions can't go missing unnoticed. A
// build without the videos (no release yet, or a local build that never fetched them) lists none and checks nothing.
//
// The transcript is the video's narration file in tools/demo-video/narration/<id>.json, as written (captions.js), with
// the on-screen text the narration doesn't say from its "onScreen". A video without a narration file has none.
import fs from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const site = path.dirname(fileURLToPath(import.meta.url));
const folder = path.join(site, 'public', 'videos');
const narrationFolder = path.join(site, '..', 'tools', 'demo-video', 'narration');
const { written } = createRequire(import.meta.url)('../tools/demo-video/captions.js');

function transcript(id) {
  const file = path.join(narrationFolder, `${id}.json`);
  if (!fs.existsSync(file)) return null;
  const { lines = {}, onScreen = {} } = JSON.parse(fs.readFileSync(file, 'utf8'));
  return Object.entries(lines).map(([key, text]) => ({ text: written(text), onScreen: onScreen[key] ?? [] }));
}

export default {
  watch: ['videos.json', 'public/videos/*', '../tools/demo-video/narration/*.json'],
  load() {
    const { release, videos } = JSON.parse(fs.readFileSync(path.join(site, 'videos.json'), 'utf8'));
    const has = (name) => fs.existsSync(path.join(folder, name));
    return videos
      .filter((video) => has(`${video.file}.mp4`))
      .map((video) => {
        const captioned = has(`${video.file}.vtt`);
        if (!captioned && video.speech !== false) {
          throw new Error(`site/public/videos/${video.file}.mp4 has no captions. Upload ${video.file}.srt (or .vtt) to the `
            + `"${release}" release next to it, or, if the video has no speech, set "speech": false on "${video.id}" in `
            + 'site/videos.json.');
        }
        return {
          ...video,
          src: `/videos/${video.file}.mp4`,
          poster: has(`${video.file}.poster.jpg`) ? `/videos/${video.file}.poster.jpg` : null,
          captions: captioned ? `/videos/${video.file}.vtt` : null,
          transcript: transcript(video.id),
        };
      });
  },
};
