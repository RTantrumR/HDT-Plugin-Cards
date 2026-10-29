#!/usr/bin/env node
// Holds the plugin's UI string catalogs (HsbgCardLookup/data/ui/<web>.json) to the English key
// source (en.json): every file must be valid JSON of string values, carry no key English lacks, and
// keep the same {n} placeholders per key. A key a language lacks renders English in the plugin, so
// it is a warning — `--strict` turns it into a failure (the gate before a release).
//
// Usage: node tools/check-ui-strings.js [--strict]

const fs = require("fs");
const path = require("path");

const dir = path.join(__dirname, "..", "HsbgCardLookup", "data", "ui");
const strict = process.argv.includes("--strict");

function load(file) {
  const raw = JSON.parse(fs.readFileSync(file, "utf8"));
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) throw new Error("not an object");
  for (const [k, v] of Object.entries(raw)) {
    if (typeof v !== "string") throw new Error(`"${k}" is not a string`);
  }
  return raw;
}

function placeholders(s) {
  const found = new Set();
  for (const m of s.matchAll(/\{(\d+)\}/g)) found.add(m[1]);
  return [...found].sort().join(",");
}

let failed = false;
const enPath = path.join(dir, "en.json");
if (!fs.existsSync(enPath)) {
  console.error("missing " + enPath);
  process.exit(1);
}
const en = load(enPath);
const enKeys = Object.keys(en);
console.log(`en.json: ${enKeys.length} keys`);

const dupes = enKeys.filter((k) => !/^[a-z][A-Za-z0-9]*(\.[a-zA-Z0-9]+)+$/.test(k));
if (dupes.length) { console.error(`en.json: ${dupes.length} key(s) not in the dotted camelCase form: ${dupes.slice(0, 10).join(", ")}`); failed = true; }
for (const [k, v] of Object.entries(en)) if (!v.trim()) { console.error(`en.json: "${k}" is empty`); failed = true; }

for (const file of fs.readdirSync(dir).filter((f) => f.endsWith(".json") && f !== "en.json").sort()) {
  const web = file.replace(/\.json$/, "");
  let cat;
  try { cat = load(path.join(dir, file)); }
  catch (e) { console.error(`${file}: ${e.message}`); failed = true; continue; }
  const unknown = Object.keys(cat).filter((k) => !(k in en));
  const missing = enKeys.filter((k) => !(k in cat));
  const badPlaceholders = Object.keys(cat).filter((k) => k in en && placeholders(cat[k]) !== placeholders(en[k]));
  const empty = Object.keys(cat).filter((k) => !cat[k].trim());
  const untranslated = Object.keys(cat).filter((k) => k in en && cat[k] === en[k] && /[a-zA-Z]{4,}/.test(en[k]));
  const parts = [`${Object.keys(cat).length} keys`, `${missing.length} missing`, `${untranslated.length} same as English`];
  console.log(`${web.padEnd(6)} ${parts.join(", ")}`);
  if (unknown.length) { console.error(`  ${web}: unknown key(s): ${unknown.join(", ")}`); failed = true; }
  if (badPlaceholders.length) { console.error(`  ${web}: placeholder mismatch in: ${badPlaceholders.join(", ")}`); failed = true; }
  if (empty.length) { console.error(`  ${web}: empty value(s): ${empty.join(", ")}`); failed = true; }
  if (missing.length && strict) { console.error(`  ${web}: missing: ${missing.join(", ")}`); failed = true; }
  else if (missing.length) console.log(`  ${web}: missing (render English): ${missing.slice(0, 8).join(", ")}${missing.length > 8 ? ", …" : ""}`);
}

if (failed) { console.error("UI strings: FAILED"); process.exit(1); }
console.log("UI strings: OK");
