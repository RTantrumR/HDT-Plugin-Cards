using System;
using System.Collections.Generic;
using System.Reflection;

namespace HsbgCardLookup.Game
{
    /// <summary>
    /// Reads, straight out of the Hearthstone client's memory, the per-opponent minion-type counts
    /// that the game's own leaderboard tile shows ("4 Beasts"). This data never reaches HDT: the
    /// server sends it as a real-time message (<c>PlayerRealTimeBattlefieldRaces</c>), not as entity
    /// tags — verified 2026-09-05 by decompiling HDT 1.57.8, HearthMirror and the HS client, and by
    /// confirming a full match's Power.log carries no such tag. The client parks it on each tile:
    /// <c>PlayerLeaderboardManager.s_instance.m_teams[].m_teamMembers[].Entry.m_overlay.m_raceCounts</c>
    /// (a <c>Map&lt;TAG_RACE,int&gt;</c>), so we walk that path.
    ///
    /// The walk rides HearthMirror's OWN memory connection: HDT hosts HearthMirror in-process
    /// (<c>Reflection.Client</c> is a <c>LocalReflectionProxy</c> over a <c>Reflection</c> instance
    /// whose private <c>Mirror.Root</c> is the ScryDotNet image), so there is no second process
    /// attach and no Unity-version string of our own to keep current — HDT maintains that. Every
    /// read is serialized against HDT's by locking the proxy, which is what the proxy itself locks.
    ///
    /// Field names are the HS client's and can move with a patch; every read is guarded and a
    /// failure degrades to "no data" (the surfaces show nothing), logged once per match.
    /// </summary>
    internal sealed class LeaderboardRaceScry
    {
        private const int TagEntityId = 53;   // GAME_TAG.ENTITY_ID — the key HDT's entity table uses

        private readonly Action<string> _log;
        private int _failuresLogged;
        private bool _loggedFirst;
        private const int MaxFailureLogs = 4;   // per match: enough to see a pattern, not a flood

        public LeaderboardRaceScry(Action<string> log) { _log = log; }

        /// <summary>Forget per-match log throttles.</summary>
        public void ResetMatch() { _failuresLogged = 0; _loggedFirst = false; _dumpedMap = false; _lastNonEmpty = -1; }

        /// <summary>Race-id → count for every leaderboard tile, keyed by the tile's hero ENTITY id.
        /// Null when the client isn't there or the walk failed; an empty inner dictionary is a tile
        /// the server hasn't sent counts for (yet). A failed walk is retried once straight away —
        /// the scry reads a live process, so a single read can land mid-mutation (HearthMirror's own
        /// proxy retries the same way).</summary>
        public Dictionary<int, Dictionary<int, int>> Read()
        {
            var client = HearthMirror.Reflection.Client;
            if (client == null) return null;
            lock (client)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    string step = "root";
                    try
                    {
                        var result = Walk(ref step);
                        int nonEmpty = 0;
                        if (result != null) foreach (var kv in result) if (kv.Value.Count > 0) nonEmpty++;
                        // Log the first read, then again whenever the number of tiles WITH counts changes
                        // (capped) — shows when the server's counts actually land on the tiles.
                        bool change = result != null && nonEmpty != _lastNonEmpty && _failuresLogged < MaxFailureLogs * 2;
                        if (result != null && ((!_loggedFirst && result.Count > 0) || change))
                        {
                            if (change && _loggedFirst) _failuresLogged++;
                            _lastNonEmpty = nonEmpty;
                            _loggedFirst = true;
                            var parts = new List<string>();
                            foreach (var kv in result)
                            {
                                var cs = new List<string>();
                                foreach (var c in kv.Value) cs.Add(c.Key + ":" + c.Value);
                                parts.Add("e" + kv.Key + "[" + string.Join(",", cs) + "]");
                            }
                            _log?.Invoke("[TribeScry] read: " + result.Count + " tiles, " + nonEmpty + " with counts - " + string.Join(" ", parts));
                        }
                        return result;
                    }
                    catch (Exception ex)
                    {
                        if (_failuresLogged < MaxFailureLogs)
                        {
                            _failuresLogged++;
                            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                            string code = inner is System.Runtime.InteropServices.ExternalException ee ? " code=0x" + ee.ErrorCode.ToString("X8") : "";
                            _log?.Invoke("[TribeScry] read failed at '" + step + "'" + (_sub != null ? " sub='" + _sub + "'" : "")
                                + " (attempt " + (attempt + 1) + "): " + inner.GetType().Name + code + ": " + inner.Message
                                + (_probe != null ? " | " + _probe : ""));
                        }
                    }
                }
                return null;
            }
        }

        // One full walk. `step` names the access in flight so a failure log says where it died.
        private Dictionary<int, Dictionary<int, int>> Walk(ref string step)
        {
            dynamic root = Root(HearthMirror.Reflection.Client);
            if (root == null) return null;
            step = "manager-class";
            dynamic mgrClass = root["PlayerLeaderboardManager"];
            step = "s_instance";
            dynamic mgr = mgrClass["s_instance"];
            if (mgr == null) return null;

            var result = new Dictionary<int, Dictionary<int, int>>();
            step = "m_teams";
            dynamic teams = mgr["m_teams"];
            foreach (var team in ListItems(teams))
            {
                if (team == null) continue;
                // The team's tiles as a plain List field. NOT via m_teamMembers[].Entry: that is an
                // auto-property whose "<Entry>k__BackingField" the scry could not resolve — every
                // live read died there (2026-09-05, SEHException 0x80004005).
                step = "m_playerLeaderboardCards";
                dynamic cards = team["m_playerLeaderboardCards"];
                foreach (var card in ListItems(cards))
                {
                    if (card == null) continue;
                    step = "m_playerHeroEntity";
                    dynamic hero = card["m_playerHeroEntity"];
                    if (hero == null) continue;

                    step = "m_tags";
                    dynamic tags = hero["m_tags"];
                    dynamic tagValues = tags == null ? null : tags["m_values"];
                    int entityId = -1;
                    step = "tag-entries";
                    foreach (var kv in DictEntries(tagValues))
                        if (ToInt(kv.Key) == TagEntityId) { entityId = ToInt(kv.Value); break; }
                    if (entityId <= 0) continue;

                    step = "m_overlay";
                    dynamic overlay = card["m_overlay"];
                    dynamic races = overlay == null ? null : overlay["m_raceCounts"];
                    var counts = new Dictionary<int, int>();
                    step = "race-entries";
                    _probe = Describe(races);
                    foreach (var kv in Entries(races))
                    {
                        int race = ToInt(kv.Key);
                        if (race >= 0) counts[race] = ToInt(kv.Value);
                    }
                    _probe = null;
                    if (!_dumpedMap && races != null) { _dumpedMap = true; _log?.Invoke("[TribeScry] map internals e" + entityId + ": " + DumpMap(races)); }
                    result[entityId] = counts;
                }
            }
            return result;
        }

        // HearthMirror.Reflection.Client → (LocalReflectionProxy<IReflection>).reflection
        //   → Reflection.Mirror (private) → Mirror.Root (public MonoImage on an internal class).
        // Fetched on every read, never cached: the proxy swaps in a fresh Reflection after a failed
        // read and Mirror.Clean() drops its root when HS exits — a cached image would go stale.
        private static object Root(object proxy)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo f = null;
            for (var t = proxy.GetType(); t != null && f == null; t = t.BaseType) f = t.GetField("reflection", any);
            var refl = f?.GetValue(proxy);
            if (refl == null) return null;
            var mirror = refl.GetType().GetProperty("Mirror", any)?.GetValue(refl);
            if (mirror == null) return null;
            return mirror.GetType().GetProperty("Root", any)?.GetValue(mirror);
        }

        // System.Collections.Generic.List<T>: _items (array) + _size.
        private static IEnumerable<dynamic> ListItems(dynamic list)
        {
            if (list == null) yield break;
            dynamic items = list["_items"];
            int size = ToInt(list["_size"]);
            if (items == null) yield break;
            for (uint i = 0; i < size; i++) yield return items[i];
        }

        // Failure diagnostics: which sub-read of a dictionary was in flight, and what the object was.
        private string _sub, _probe;
        private bool _dumpedMap;
        private int _lastNonEmpty = -1;

        // Raw internals of one Blizzard Map, once per match: is it really empty, or is the occupancy
        // test wrong? Prints the field values and the first slots' raw HashCode/key/value with their
        // .NET types as the scry hands them back.
        private static string DumpMap(dynamic map)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in new[] { "count", "touchedSlots", "emptySlot", "threshold" })
            {
                try { object v = map[f]; sb.Append(f).Append('=').Append(v == null ? "null" : v.ToString()).Append(' '); }
                catch (Exception ex) { sb.Append(f).Append("=ERR:").Append(ex.GetType().Name).Append(' '); }
            }
            try
            {
                dynamic links = map["linkSlots"]; dynamic keys = map["keySlots"]; dynamic vals = map["valueSlots"];
                sb.Append("links=").Append(links == null ? "null" : ((object)links.size()).ToString());
                sb.Append(" keys=").Append(keys == null ? "null" : ((object)keys.size()).ToString());
                sb.Append(" vals=").Append(vals == null ? "null" : ((object)vals.size()).ToString());
                uint n = links == null ? 0u : Math.Min(4u, (uint)links.size());
                for (uint i = 0; i < n; i++)
                {
                    sb.Append(" [").Append(i).Append("] ");
                    try { dynamic link = links[i]; object hc = link == null ? null : (object)link["HashCode"]; object nx = link == null ? null : (object)link["Next"]; sb.Append("hc=").Append(Fmt(hc)).Append(" next=").Append(Fmt(nx)); }
                    catch (Exception ex) { sb.Append("link=ERR:").Append(ex.GetType().Name); }
                    try { object k = keys[i]; sb.Append(" key=").Append(Fmt(k)); } catch (Exception ex) { sb.Append(" key=ERR:").Append(ex.GetType().Name); }
                    try { object v = vals[i]; sb.Append(" val=").Append(Fmt(v)); } catch (Exception ex) { sb.Append(" val=ERR:").Append(ex.GetType().Name); }
                }
            }
            catch (Exception ex) { sb.Append(" arrays=ERR:").Append(ex.GetType().Name).Append(':').Append(ex.Message); }
            return sb.ToString();
        }

        private static string Fmt(object v) => v == null ? "null" : v + "(" + v.GetType().Name + ")";

        // Class name (and parent) plus field names of a scry object — what the failure log needs to
        // tell a wrong field name from a type the scry can't resolve. Every part guarded.
        private static string Describe(dynamic obj)
        {
            if (obj == null) return "obj=null";
            var sb = new System.Text.StringBuilder();
            try { sb.Append("class=").Append((string)obj.Class.FullName); } catch { sb.Append("class=?"); }
            try { sb.Append(" parent=").Append((string)obj.Class.Parent.FullName); } catch { sb.Append(" parent=?"); }
            try
            {
                Dictionary<string, object> fields = obj.getFields();
                sb.Append(" fields=[").Append(string.Join(",", fields.Keys)).Append("]");
            }
            catch (Exception ex) { sb.Append(" fields=ERR:" + ex.GetType().Name); }
            return sb.ToString();
        }

        // Two hash-map layouts live in the client. System Dictionary<K,V> (Entity.m_tags.m_values) and
        // Blizzard's own Blizzard.T5.Core.Map<K,V> (m_raceCounts) — the latter is NOT a Dictionary
        // subclass but a copy of the OLD Mono dictionary (found live 2026-09-05: fields count,
        // emptySlot, generation, hcp, keySlots, linkSlots, table, threshold, touchedSlots, valueSlots).
        private IEnumerable<KeyValuePair<object, object>> Entries(dynamic map)
        {
            if (map == null) return System.Linq.Enumerable.Empty<KeyValuePair<object, object>>();
            string cls = null;
            try { cls = (string)map.Class.FullName; } catch { }
            return cls != null && cls.StartsWith("Blizzard.T5.Core.Map", StringComparison.Ordinal)
                ? MapEntries(map) : DictEntries(map);
        }

        // Old-Mono dictionary layout: parallel arrays keySlots[] / valueSlots[] / linkSlots[] (Link
        // { HashCode, Next }) over touchedSlots slots; a slot is in use when its HashCode carries the
        // HASH_FLAG top bit (int.MinValue), which is how that implementation marks occupancy.
        private IEnumerable<KeyValuePair<object, object>> MapEntries(dynamic map)
        {
            _sub = "touchedSlots";
            int touched = ToInt(map["touchedSlots"]);
            _sub = "linkSlots";
            dynamic links = map["linkSlots"];
            _sub = "keySlots";
            dynamic keys = map["keySlots"];
            _sub = "valueSlots";
            dynamic vals = map["valueSlots"];
            if (links == null || keys == null || vals == null) { _sub = null; yield break; }
            for (uint i = 0; i < touched; i++)
            {
                _sub = "linkSlots[" + i + "]";
                dynamic link = links[i];
                if (link == null) continue;
                _sub = "HashCode";
                long hc = ToLong(link["HashCode"]);
                if ((hc & 0x80000000L) == 0) continue;   // free slot
                _sub = "keySlots[" + i + "]";
                object key = keys[i];
                // An enum key arrives as a struct wrapper (read via its value__ field in ToLong); if
                // even that fails, the slot's HashCode is the enum's own value under the flag bit —
                // verified live (0x8000005C → 92 = NAGA, 0x8000000E → 14 = MURLOC).
                if (ToLong(key) < 0) key = (int)(hc & 0x7FFFFFFFL);
                _sub = "valueSlots[" + i + "]";
                object value = vals[i];
                _sub = null;
                yield return new KeyValuePair<object, object>(key, value);
            }
            _sub = null;
        }

        // System Dictionary<K,V> (Unity's corefx-derived corlib): _entries (Entry[] { hashCode, next,
        // key, value }) + _count; a freed slot has next < -1 (corefx) or hashCode < 0
        // (referencesource) — both checked so either corlib flavour reads right.
        private IEnumerable<KeyValuePair<object, object>> DictEntries(dynamic dict)
        {
            if (dict == null) yield break;
            _sub = "_count";
            int count = ToInt(dict["_count"]);
            _sub = "_entries";
            dynamic entries = dict["_entries"];
            if (entries == null) { _sub = null; yield break; }
            for (uint i = 0; i < count; i++)
            {
                _sub = "entries[" + i + "]";
                dynamic e = entries[i];
                if (e == null) continue;
                _sub = "next";
                long next = ToLong(e["next"]);
                _sub = "hashCode";
                long hash = ToLong(e["hashCode"]);
                if (next < -1 || hash < 0) continue;
                _sub = "key";
                object key = e["key"];
                _sub = "value";
                object value = e["value"];
                _sub = null;
                yield return new KeyValuePair<object, object>(key, value);
            }
            _sub = null;
        }

        private static int ToInt(object v) => (int)ToLong(v);

        // TAG_RACE names, in case the scry hands an enum-typed slot back as its name rather than its
        // number (same ids as HearthDb.Race). Unknown names → -1, which callers skip.
        private static readonly Dictionary<string, int> RaceByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "INVALID", 0 }, { "UNDEAD", 11 }, { "MURLOC", 14 }, { "DEMON", 15 }, { "MECHANICAL", 17 },
            { "ELEMENTAL", 18 }, { "BEAST", 20 }, { "PET", 20 }, { "PIRATE", 23 }, { "DRAGON", 24 },
            { "BLANK", 25 }, { "ALL", 26 }, { "QUILBOAR", 43 }, { "NAGA", 92 },
        };

        private static long ToLong(object v)
        {
            if (v == null) return 0;
            if (v is int i) return i;
            if (v is uint u) return u;
            if (v is long l) return l;
            if (v is short s) return s;
            if (v is ushort us) return us;
            if (v is byte b) return b;
            if (v is sbyte sb) return sb;
            if (v is Enum) return Convert.ToInt64(v);
            if (v is string str)
            {
                if (long.TryParse(str, out var p)) return p;
                return RaceByName.TryGetValue(str.Trim(), out var r) ? r : -1;
            }
            if (v is IConvertible) { try { return Convert.ToInt64(v); } catch { return -1; } }
            // A scry struct wrapper — for an enum that is the boxed enum itself, whose one field is
            // value__ (the underlying integer). Seen live: TAG_RACE keys come back this way.
            try
            {
                dynamic d = v;
                object inner = d["value__"];
                if (inner is IConvertible || inner is string) return ToLong(inner);
            }
            catch { }
            return -1;
        }
    }
}
