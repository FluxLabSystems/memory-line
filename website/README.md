# Documentation website

A static website generated from the markdown documentation in this repository, so the
13 READMEs, guides, audits and design notes covering the **Windows Native app** can be
browsed, cross-linked and searched instead of opened one file at a time.

The built site lives in [`_site/`](./_site) and is committed, so you can browse it
without building anything: **open `website/_site/index.html` in a browser.**

---

## Scope

The site documents the Windows Native (.NET 10 / WinUI 3) app — the primary, actively
developed product — plus the shared design and audit documents.

Not every markdown file in the repository becomes a page. `site.config.mjs` decides:
a document is on the site only if it appears in some section's `pages` array. Links
*to* a repository file that is not on the site resolve to GitHub automatically, so
nothing is unreachable — it just does not get page treatment.

To add a document, drop an entry into the relevant section's `pages` array.

---

## What it generates

| Page | Source |
|------|--------|
| `index.html` | Hand-authored landing page — pipeline diagram, feature grid, architecture, reading paths, project history (content lives in `site.config.mjs`) |
| `documentation-map.html` | Generated index of every included document with size, reading time and section previews |
| One page per document | Every markdown file listed in a section's `pages` array in `site.config.mjs` |

Each document page gets a breadcrumb, description, word count and reading time, a link
to the source file on GitHub, an "on this page" table of contents with scrollspy, and
previous/next navigation.

Site-wide: client-side full-text search (`/` or <kbd>Ctrl</kbd>/<kbd>Cmd</kbd>+<kbd>K</kbd>),
a light/dark theme toggle that remembers your choice, and a responsive layout that
collapses to a drawer on small screens.

Markdown gets some help along the way:

- **Stable heading anchors** using GitHub's slug algorithm, so existing `#in-document`
  links keep working.
- **Repo-aware links** — links to a documented `.md` file become site links; links to
  any other repo path become GitHub links; external links open in a new tab.
- **Syntax highlighting** for PowerShell, bash, C#, JavaScript, JSON, XML/XAML, SQL and
  YAML, with a copy button on every code block.
- **ASCII diagrams and directory trees** are detected and rendered as figures rather
  than coloured as code.
- **Callouts** — blockquotes starting with *Note*, *Important*, *Warning* or *Tip*
  become styled admonitions.
- **Status chips** — `✅` / `🔄` / `⚠️` / `❌` at the start of a table cell become
  coloured chips.

There are no runtime dependencies and no external requests: the output works over
`file://` as well as from a web server.

---

## Building

The docs toolchain is self-contained: `website/` carries its own `package.json`, and
the site has one small dependency (`marked`).

```bash
cd website
npm install          # one small dependency: marked
npm run build        # writes ./_site
npm run serve        # preview at http://localhost:4173
```

Or from the repository root:

```bash
npm --prefix website install
npm --prefix website run build
npm --prefix website run serve
```

To write the output somewhere else, pass the flag through npm with a `--`
separator: `npm run build -- --out DIR` (or call the script directly,
`node build.mjs --out DIR`).

> **Note:** `_site/` is generated output. Rebuild and commit it whenever you change a
> markdown document, or the published site will drift from the source. The footer's
> "generated" date comes from the newest git commit touching the site's sources, not
> from the clock, so rebuilding unchanged sources produces a byte-identical `_site`.

---

## Adding or changing a document

Everything is driven by [`site.config.mjs`](./site.config.mjs).

To add a document, drop an entry into the relevant section's `pages` array:

```js
{
  slug: 'my-page',                  // becomes my-page.html
  source: 'docs/my-page.md',        // repo-relative path
  title: 'My page',
  description: 'One or two sentences shown in the header, cards and search.',
  audience: 'Developers',
  tags: ['setup'],
}
```

Sections themselves are the top-level entries of the exported `sections` array — the
order there is the order in the sidebar. The landing page's content (stats, pipeline
steps, feature groups, architecture notes, reading paths, roadmap) is the exported
`landing` object in the same file; the document and word counts in the stats strip are
filled in from the real corpus at build time.

A configured file that no longer exists is skipped with a warning rather than failing
the build.

---

## Layout

```
website/
├── build.mjs           orchestration: read → render → write
├── serve.mjs           minimal static preview server
├── site.config.mjs     navigation, per-document metadata, landing page content
├── lib/
│   ├── render.mjs      markdown → HTML, TOC extraction, link rewriting
│   ├── templates.mjs   page shell, landing page, documentation map
│   └── highlight.mjs   dependency-free syntax highlighter
├── assets/
│   ├── styles.css      themes and layout
│   └── app.js          theme, navigation, copy buttons, scrollspy, search
└── _site/              generated output (committed)
```

---

## Publishing

`.github/workflows/docs-site.yml` builds the site and deploys it to GitHub Pages on
every push to `main` that touches documentation, and on manual dispatch. It requires
**Settings → Pages → Source: GitHub Actions** to be enabled on the repository; until
then the deploy step will fail while the build step still verifies the site compiles.
