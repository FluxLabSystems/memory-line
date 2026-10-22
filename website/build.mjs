#!/usr/bin/env node
/**
 * Build the Memory Timeline documentation website.
 *
 *   node build.mjs            # writes ./_site
 *   node build.mjs --out DIR  # writes DIR
 *
 * Every markdown document listed in site.config.mjs becomes a page, plus two
 * generated pages: the landing page and the documentation map. Output is a plain
 * static site with no runtime dependencies — it works over file:// as well as
 * from a web server.
 */

import { execFileSync } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { landing, sections, site } from './site.config.mjs';
import { renderMarkdown, toPlainText } from './lib/render.mjs';
import {
  layout,
  renderDocHeader,
  renderDocMap,
  renderLanding,
  renderPager,
  renderSidebar,
  renderToc,
} from './lib/templates.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..');

const outArgIndex = process.argv.indexOf('--out');
const outDir =
  outArgIndex > -1 && process.argv[outArgIndex + 1]
    ? path.resolve(process.cwd(), process.argv[outArgIndex + 1])
    : path.join(here, '_site');

const WORDS_PER_MINUTE = 220;

async function main() {
  const started = Date.now();

  /* ---- flatten the page list ------------------------------------- */
  const pages = [];
  for (const section of sections) {
    for (const page of section.pages) {
      pages.push({ ...page, sectionId: section.id, sectionTitle: section.title });
    }
  }

  const pageIndex = new Map(pages.map((p) => [p.slug, p]));
  const pathToSlug = new Map();
  for (const page of pages) {
    if (page.source) pathToSlug.set(page.source.replace(/^\.\//, ''), page.slug);
  }

  /* ---- render every markdown document ---------------------------- */
  const searchIndex = [];
  let totalWords = 0;
  let missing = 0;

  for (const page of pages) {
    if (!page.source) continue;

    const absolute = path.join(repoRoot, page.source);
    let markdown;
    try {
      markdown = await fs.readFile(absolute, 'utf8');
    } catch {
      console.warn(`  ! missing source: ${page.source} — skipping`);
      page.missing = true;
      missing += 1;
      continue;
    }

    const { html, toc } = renderMarkdown(markdown, {
      sourceDir: path.posix.dirname(page.source.split(path.sep).join('/')),
      pathToSlug,
      repoUrl: site.repoUrl,
      branch: site.branch,
    });

    // The doc header already prints the title, so drop the leading H1.
    const body = html.replace(/^\s*<h1\b[^>]*>[\s\S]*?<\/h1>\s*/, '');

    const plain = toPlainText(body);
    const words = plain ? plain.split(/\s+/).length : 0;
    totalWords += words;

    page.html = body;
    page.toc = toc;
    page.stats = {
      words,
      minutes: Math.max(1, Math.round(words / WORDS_PER_MINUTE)),
      headings: toc.length,
      bytes: Buffer.byteLength(markdown),
    };

    searchIndex.push(...indexPage(page, body));
  }

  const livePages = pages.filter((p) => !p.missing);
  const liveSections = sections
    .map((s) => ({
      ...s,
      pages: s.pages
        .filter((p) => !pageIndex.get(p.slug)?.missing)
        .map((p) => pageIndex.get(p.slug)),
    }))
    .filter((s) => s.pages.length);

  const docPages = livePages.filter((p) => p.source);
  const totals = {
    docs: docPages.length,
    words: totalWords,
    minutes: Math.round(totalWords / WORDS_PER_MINUTE),
  };

  const buildTime = generatedDate(docPages);

  // Fill the landing-page counters from the corpus we just rendered.
  const statValues = {
    docs: String(totals.docs),
    words: totals.words >= 1000 ? `${Math.round(totals.words / 1000)}k` : String(totals.words),
  };
  const landingWithStats = {
    ...landing,
    stats: landing.stats.map((s) => (s.key && statValues[s.key] ? { ...s, value: statValues[s.key] } : s)),
  };

  const shellCommon = {
    siteName: site.name,
    repoUrl: site.repoUrl,
    buildTime,
    pageCount: totals.docs,
  };

  /* ---- write output ---------------------------------------------- */
  await fs.rm(outDir, { recursive: true, force: true });
  await fs.mkdir(path.join(outDir, 'assets'), { recursive: true });

  // Document pages
  for (let i = 0; i < livePages.length; i += 1) {
    const page = livePages[i];
    if (!page.source) continue;

    const prev = findNeighbour(livePages, i, -1);
    const next = findNeighbour(livePages, i, 1);

    const content = `
${renderDocHeader(page, page.stats, site.repoUrl, site.branch)}
<div class="doc-grid">
  <article class="prose">
${page.html}
${renderPager(prev, next)}
  </article>
${renderToc(page.toc)}
</div>`;

    await write(
      path.join(outDir, `${page.slug}.html`),
      layout({
        ...shellCommon,
        title: `${page.title} · ${site.name} docs`,
        description: page.description,
        bodyClass: 'page-doc',
        activeSlug: page.slug,
        sidebar: renderSidebar(liveSections, page.slug, page.toc),
        content,
      })
    );
  }

  // Landing page
  await write(
    path.join(outDir, 'index.html'),
    layout({
      ...shellCommon,
      title: `${site.name} — documentation`,
      description: site.description,
      bodyClass: 'page-landing',
      activeSlug: 'index',
      sidebar: renderSidebar(liveSections, 'index', null),
      content: renderLanding({
        landing: landingWithStats,
        site,
        sections: liveSections,
        pageIndex,
        totals,
      }),
    })
  );

  // Documentation map
  await write(
    path.join(outDir, 'documentation-map.html'),
    layout({
      ...shellCommon,
      title: `All documentation · ${site.name}`,
      description: `Every ${site.name} Windows Native document, with size, reading time and section previews.`,
      bodyClass: 'page-map',
      activeSlug: 'documentation-map',
      sidebar: renderSidebar(liveSections, 'documentation-map', null),
      content: renderDocMap({
        sections: liveSections,
        totals,
        repoUrl: site.repoUrl,
        branch: site.branch,
      }),
    })
  );

  // Static assets
  for (const file of ['styles.css', 'app.js']) {
    await fs.copyFile(path.join(here, 'assets', file), path.join(outDir, 'assets', file));
  }

  await write(
    path.join(outDir, 'assets', 'search-index.js'),
    `window.MT_SEARCH_INDEX=${JSON.stringify(searchIndex)};\n` +
      `window.MT_SEARCH_PAGES=${JSON.stringify(
        Object.fromEntries(docPages.map((p) => [p.slug, p.title]))
      )};\n`
  );

  // GitHub Pages: do not run the output through Jekyll.
  await write(path.join(outDir, '.nojekyll'), '');

  const seconds = ((Date.now() - started) / 1000).toFixed(2);
  console.log(
    `Built ${totals.docs + 2} pages (${totals.docs} documents, ${searchIndex.length} search sections, ` +
      `${totals.words.toLocaleString('en-US')} words) → ${path.relative(process.cwd(), outDir) || outDir} in ${seconds}s`
  );
  if (missing) console.log(`  ${missing} configured source file(s) were missing and were skipped.`);
}

/**
 * The footer's "generated" date: the commit date (YYYY-MM-DD) of the newest
 * git commit touching anything the build reads — the configured markdown
 * sources plus the generator itself (templates, styles, scripts, config).
 *
 * Deriving it from git rather than the clock makes the output a pure function
 * of the sources: rebuilding unchanged sources on another day yields a
 * byte-identical _site, so two documentation PRs built on different days no
 * longer conflict in every page's footer. Falls back to today's date when git
 * is unavailable or this is not a checkout (an exported tarball, a CI image
 * without git), and `_site` itself is deliberately not an input — committing a
 * rebuild must not move the date.
 */
function generatedDate(docPages) {
  const today = new Date().toISOString().slice(0, 10);
  const inputs = [
    ...docPages.map((p) => p.source),
    'website/build.mjs',
    'website/site.config.mjs',
    'website/package.json',
    'website/lib',
    'website/assets',
  ];
  try {
    const out = execFileSync('git', ['-C', repoRoot, 'log', '-1', '--format=%cs', '--', ...inputs], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'ignore'],
      timeout: 10_000,
    }).trim();
    return /^\d{4}-\d{2}-\d{2}$/.test(out) ? out : today;
  } catch {
    return today;
  }
}

/** Split a rendered document into H2 sections for the search index. */
function indexPage(page, html) {
  const entries = [];
  const chunks = html.split(/(?=<h2\b)/);

  for (const chunk of chunks) {
    const match = chunk.match(/^<h2\b[^>]*id="([^"]+)"[^>]*>([\s\S]*?)<\/h2>/);
    const anchor = match ? match[1] : '';
    const heading = match ? stripToText(match[2]) : '';
    const text = toPlainText(match ? chunk.slice(match[0].length) : chunk);
    if (!heading && !text) continue;
    entries.push({
      p: page.slug,
      t: page.title,
      s: page.sectionTitle,
      h: heading || page.title,
      a: anchor,
      x: (heading ? '' : `${page.description} `) + text.slice(0, 4000),
    });
  }

  return entries;
}

function stripToText(html) {
  return html
    .replace(/<a class="heading-anchor"[\s\S]*?<\/a>/g, '')
    .replace(/<[^>]*>/g, '')
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .trim();
}

function findNeighbour(list, index, step) {
  for (let i = index + step; i >= 0 && i < list.length; i += step) {
    if (list[i].source) return list[i];
  }
  return null;
}

async function write(file, contents) {
  await fs.mkdir(path.dirname(file), { recursive: true });
  await fs.writeFile(file, contents, 'utf8');
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
