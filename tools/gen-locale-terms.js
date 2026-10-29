#!/usr/bin/env node
// Generates HsbgCardLookup/data/locale-terms.json — the words the plugin's search and filter
// labels understand in each of the site's 12 locales — from a local checkout of the hsbg.cards
// website repo (the same data the site's own localized card pages use):
//
//   data/production/terms.json       the game's own words: tribes, card types, spell schools,
//                                    trinket tiers and keywords, with the client's label per locale
//   lib/i18n/catalogs/<web>.ts       the site's category words (buddies / tokens / Dark Gift)
//
// Usage:  node tools/gen-locale-terms.js <path-to-hsbgcards-checkout>
//
// Only labels that differ from English are written; the plugin falls back to the English value
// for anything missing, so a locale the table has no word for simply keeps the English word.
// Re-run after every site import that changes terms.json, and ship the result with the plugin
// (it is copied next to the DLL as data\locale-terms.json).

const fs = require("fs");
const path = require("path");

const site = process.argv[2];
if (!site) {
  console.error("usage: node tools/gen-locale-terms.js <path-to-hsbgcards-checkout>");
  process.exit(2);
}

const WEBS = ["de", "es", "es-mx", "fr", "it", "ja", "ko", "pl", "pt", "th", "zh-cn", "zh-tw"];
// terms.json kind → the plugin's vocabulary group.
const KIND_MAP = {
  keyword: "keyword",
  tribe: "tribe",
  card_type: "cardType",
  spell_school: "spellSchool",
  trinket_tier: "trinketTier",
};
// Catalog key → the plugin's category canonical (lib/search/locale-aliases.ts CATEGORY_KEYS).
const CATEGORY_KEYS = [["buddy", "buddies"], ["token", "tokens"], ["darkgift", "spellDarkGift"]];

const termsPath = path.join(site, "data", "production", "terms.json");
const terms = JSON.parse(fs.readFileSync(termsPath, "utf8"));

function catalogStrings(web) {
  const file = path.join(site, "lib", "i18n", "catalogs", web + ".ts");
  if (!fs.existsSync(file)) return {};
  const src = fs.readFileSync(file, "utf8");
  const out = {};
  for (const [, key] of CATEGORY_KEYS) {
    const m = src.match(new RegExp("^\\s*" + key + "\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", "m"));
    if (m) out[key] = JSON.parse('"' + m[1] + '"');
  }
  // A catalog may spread another one (es-mx spreads es): keys it lacks come from there.
  const spread = src.match(/\.\.\.([A-Z_]+)\s*,/);
  if (spread) {
    const base = spread[1].toLowerCase().replace("_", "-");
    const baseStrings = catalogStrings(base);
    for (const k of Object.keys(baseStrings)) if (out[k] === undefined) out[k] = baseStrings[k];
  }
  return out;
}

const en = catalogStrings("en");
const same = (a, b) => (a || "").trim().toLowerCase() === (b || "").trim().toLowerCase();

const locales = {};
for (const web of WEBS) {
  const vocab = { keyword: {}, tribe: {}, cardType: {}, spellSchool: {}, trinketTier: {}, category: {} };
  for (const row of terms.terms) {
    const group = KIND_MAP[row.kind];
    if (!group) continue;
    const label = row.labels && row.labels[web];
    if (!label || same(label, row.value) || same(label, row.name)) continue;
    vocab[group][row.value] = label.trim();
  }
  const cat = catalogStrings(web);
  for (const [canonical, key] of CATEGORY_KEYS) {
    const label = cat[key];
    if (!label || same(label, en[key]) || same(label, canonical)) continue;
    vocab.category[canonical] = label.trim();
  }
  locales[web] = vocab;
}

const out = {
  format: 1,
  patch: terms.patch,
  generated: new Date().toISOString().slice(0, 10),
  source: "hsbg.cards data/production/terms.json + lib/i18n/catalogs (tools/gen-locale-terms.js)",
  locales,
};

const dest = path.join(__dirname, "..", "HsbgCardLookup", "data", "locale-terms.json");
fs.writeFileSync(dest, JSON.stringify(out, null, 1) + "\n", "utf8");

for (const web of WEBS) {
  const v = locales[web];
  const n = Object.values(v).reduce((a, g) => a + Object.keys(g).length, 0);
  console.log(web.padEnd(6), n, "labels");
}
console.log("wrote", path.relative(process.cwd(), dest));
