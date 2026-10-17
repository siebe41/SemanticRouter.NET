# The documentation site

The site at <https://siebe41.github.io/SemanticRouter.NET/> is built from the repository's own Markdown with
[VitePress](https://vitepress.dev) and published to GitHub Pages by [pages.yml](../.github/workflows/pages.yml). It
updates whenever a change to the docs reaches `main`.

Nothing is copied into `site/`. The README, `docs/`, the samples' READMEs, the changelog and the governance files stay
where they are and read the same on GitHub: [.vitepress/config.mjs](.vitepress/config.mjs) lists which files are pages
and at which address (`docs/host.md` is `/host`, the README is `/overview`), turns a link between two of them into a
link between their pages, and sends any other relative link (code, workflows, `LICENSE`) to the file on GitHub. Every
other Markdown file stays off the site, this one included. `site/` itself holds only the [home page](index.md), the
[videos page](videos.md), the theme and the logo.

| To | Do |
|---|---|
| Add a guide | Add `docs/<name>.md`: it is on the site at `/<name>`. Add it to the sidebar in `config.mjs`, and to the [list of guides](../docs/README.md) in `docs/README.md`. |
| Preview the site | `cd site && npm ci && npm run dev`, then open the address it prints. `npm run build` is what the workflow runs: it fails on a dead link. |
| Add a video | See below. |

## Videos

The videos are too large for the repository, so they live as assets on a GitHub release tagged `media`, a pre-release
so it is never the latest release and the desktop app's updater ignores it. The workflow downloads them into the site
when it builds ([fetch-videos.sh](fetch-videos.sh)). The tag doesn't start with `v`, because a `v*` tag starts the
release workflow.

[videos.json](videos.json) lists the videos the page can show, in order, each with a file name, title, length and a
sentence about it. A video is on the page once `<file>.mp4` is on the release; `<file>.poster.jpg` becomes its
thumbnail, and `<file>.srt` or `<file>.vtt` its captions. The names match what the demo-video tools write
(`semantic-router-demo.mp4` and its `.srt` for the intro).

Captions are required: the build fails when a video's MP4 is on the release without them, unless its entry in
`videos.json` says `"speech": false`. A build without any videos (no release yet, or a local preview that never ran
`fetch-videos.sh`) checks nothing. Under each video is a transcript, made from its narration file in
[tools/demo-video/narration](../tools/demo-video/narration) (the video's `id`), with the figures on screen that the
narration doesn't say from that file's `onScreen` ([videos.data.js](videos.data.js)).

```bash
# Once: the release that holds the videos.
gh release create media --prerelease --title "Site media" --notes "Videos for the documentation site (site/README.md)."

# Each time: upload (or replace) a video, its poster and its captions, then publish the site.
gh release upload media out/semantic-router-demo.mp4 out/semantic-router-demo.poster.jpg out/semantic-router-demo.srt --clobber
gh workflow run pages.yml
```

A poster frame can be cut from the video: `ffmpeg -ss 4 -i video.mp4 -frames:v 1 -q:v 3 video.poster.jpg`. Keep each
file under 100 MB (GitHub Pages' limit per file); H.264 MP4 at 1080p is a few megabytes a minute.

## Setup, once

**Settings → Pages → Build and deployment → Source: GitHub Actions.** The first run of the workflow on `main` then
publishes the site and creates the `github-pages` environment, which only `main` can deploy to. Add the address to the
repository's About box (the gear next to it → *Use your GitHub Pages website*).
