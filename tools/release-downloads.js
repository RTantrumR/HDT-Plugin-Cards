#!/usr/bin/env node
// Download counts of every GitHub release asset, with the release's age and downloads per day.
// A download is not an install, but the per-day rate of the current release is the best running
// estimate of how many players use the plugin: most of them update within days of a release.
//
// Usage: node tools/release-downloads.js            every release, newest first
//        node tools/release-downloads.js v0.8.1     one release
//        node tools/release-downloads.js --json     raw rows for a spreadsheet
// Needs the GitHub CLI signed in (`gh auth status`).

const { execFileSync } = require("child_process");

const REPO = "RTantrumR/HDT-Plugin-Cards";
const args = process.argv.slice(2);
const json = args.includes("--json");
const onlyTag = args.find((a) => !a.startsWith("--"));

let releases;
try {
  releases = JSON.parse(execFileSync("gh", ["api", `repos/${REPO}/releases?per_page=100`], { encoding: "utf8" }));
} catch (e) {
  console.error("gh api failed (is the GitHub CLI installed and signed in?):\n" + (e.stderr || e.message));
  process.exit(1);
}
if (onlyTag) releases = releases.filter((r) => r.tag_name === onlyTag);
if (!releases.length) { console.error("no release" + (onlyTag ? " tagged " + onlyTag : "s")); process.exit(1); }

// A release is downloaded while it is the latest one, so its rate is measured over that window:
// from its publish time to the next release's (or to now for the current one). Lifetime age
// would make every old release look slower the longer it sits there.
const now = Date.now();
const byDate = [...releases].sort((a, b) => new Date(a.published_at) - new Date(b.published_at));
const windowEnd = new Map();
byDate.forEach((r, i) => windowEnd.set(r.tag_name, i + 1 < byDate.length ? new Date(byDate[i + 1].published_at).getTime() : now));
const rows = releases.map((r) => {
  const published = new Date(r.published_at);
  const windowDays = (windowEnd.get(r.tag_name) - published.getTime()) / 86400000;
  const assets = (r.assets || []).map((a) => ({ name: a.name, downloads: a.download_count }));
  const total = assets.reduce((s, a) => s + a.downloads, 0);
  return { tag: r.tag_name, published: published.toISOString().slice(0, 10), windowDays, total, perDay: total / Math.max(windowDays, 1 / 24), assets };
});

if (json) { console.log(JSON.stringify(rows, null, 2)); process.exit(0); }

const span = (d) => (d < 1 ? `${Math.round(d * 24)} h` : `${d.toFixed(1)} d`);
for (const r of rows) {
  console.log(`${r.tag}  published ${r.published}  latest for ${span(r.windowDays)}  downloads ${r.total}  (${r.perDay.toFixed(1)}/day while latest)`);
  for (const a of r.assets) console.log(`    ${a.downloads.toString().padStart(6)}  ${a.name}`);
}
const all = rows.reduce((s, r) => s + r.total, 0);
if (rows.length > 1) console.log(`\n${rows.length} releases, ${all} downloads in all`);
