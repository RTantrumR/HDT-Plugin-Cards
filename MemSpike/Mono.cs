using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MemSpike
{
    /// <summary>
    /// Walks the Mono runtime inside a Unity process: root domain → assemblies → image → class
    /// cache → fields → static data → objects.
    ///
    /// Struct offsets are NOT hard-coded from a Mono version. Each one is found at attach time by
    /// scanning for a slot whose content satisfies an invariant of the runtime (a back-pointer, a
    /// readable name, a count that adds up), and the result is reported by <see cref="Describe"/>.
    /// A layout the scan cannot make sense of fails loudly at <see cref="Attach"/>.
    /// </summary>
    internal sealed class Mono
    {
        public readonly Mem M;
        private long _monoBase, _monoEnd;

        public long RootDomain, Image;
        public int OffAsmImage, OffClassCache, OffName, OffNextClass, OffFields, OffRuntimeInfo, OffVtSize, OffVtArray;
        public int CacheEntries, ClassesFound, AssemblyCount;
        private readonly Dictionary<string, long> _classes = new Dictionary<string, long>();
        private readonly Dictionary<long, List<Field>> _fields = new Dictionary<long, List<Field>>();

        public struct Field { public string Name; public int Offset; public bool Static; }

        public Mono(Mem m) { M = m; }

        // ── attach ──────────────────────────────────────────────────────────────────────────────
        public void Attach(Process p, string assembly)
        {
            foreach (ProcessModule mod in p.Modules)
                if (mod.ModuleName.StartsWith("mono-2.0", StringComparison.OrdinalIgnoreCase))
                { _monoBase = (long)mod.BaseAddress; _monoEnd = _monoBase + mod.ModuleMemorySize; }
            if (_monoBase == 0) throw new InvalidOperationException("mono runtime module not loaded");

            long fn = Export("mono_get_root_domain");
            if (fn == 0) throw new InvalidOperationException("mono_get_root_domain export not found");
            // The getter is `mov rax, [rip+disp32]; ret` — read the global it returns.
            var code = M.Bytes(fn, 8);
            if (code == null || code[0] != 0x48 || code[1] != 0x8B || code[2] != 0x05 || code[7] != 0xC3)
                throw new InvalidOperationException("unexpected mono_get_root_domain body: " + Hex(code));
            RootDomain = M.Ptr(fn + 7 + BitConverter.ToInt32(code, 3));
            if (RootDomain == 0) throw new InvalidOperationException("root domain is null");

            long asm = FindAssembly(assembly);
            if (asm == 0) throw new InvalidOperationException("assembly '" + assembly + "' not found in the root domain (" + AssemblyCount + " seen)");
            Image = FindImage(asm, assembly);
            if (Image == 0) throw new InvalidOperationException("image of '" + assembly + "' not found");
            LoadClasses();
        }

        // PE export table of the mono module, read from the target's memory.
        private long Export(string name)
        {
            long nt = _monoBase + M.I32(_monoBase + 0x3C);
            long exp = _monoBase + (uint)M.I32(nt + 0x18 + 0x70);          // PE32+ data directory 0
            int count = M.I32(exp + 0x18);
            long funcs = _monoBase + (uint)M.I32(exp + 0x1C), names = _monoBase + (uint)M.I32(exp + 0x20), ords = _monoBase + (uint)M.I32(exp + 0x24);
            var nameRvas = M.Bytes(names, count * 4);
            if (nameRvas == null) return 0;
            for (int i = 0; i < count; i++)
            {
                if (M.CStr(_monoBase + BitConverter.ToUInt32(nameRvas, i * 4)) != name) continue;
                int ord = M.U16(ords + i * 2);
                return _monoBase + (uint)M.I32(funcs + ord * 4);
            }
            return 0;
        }

        // domain->domain_assemblies is a GSList {data, next} of MonoAssembly*; MonoAssembly keeps
        // aname.name (char*) at 0x10. The slot in MonoDomain is whichever one walks as such a list.
        private long FindAssembly(string wanted)
        {
            for (int off = 0; off < 0x400; off += 8)
            {
                long node = M.Ptr(RootDomain + off), hit = 0;
                int n = 0; bool corlib = false;
                for (; node != 0 && n < 2000; node = M.Ptr(node + 8), n++)
                {
                    long a = M.Ptr(node);
                    string nm = M.CStr(M.Ptr(a + 0x10), 100);
                    if (string.IsNullOrEmpty(nm)) { n = -1; break; }
                    if (nm == "mscorlib") corlib = true;
                    if (nm == wanted) hit = a;
                }
                if (n >= 10 && corlib) { AssemblyCount = n; return hit; }
            }
            return 0;
        }

        // MonoAssembly->image: the slot whose target carries the assembly's own file name.
        private long FindImage(long asm, string name)
        {
            for (int off = 0x18; off < 0x100; off += 8)
            {
                long img = M.Ptr(asm + off);
                if (img == 0) continue;
                for (int s = 0x8; s < 0x60; s += 8)
                {
                    string str = M.CStr(M.Ptr(img + s), 400);
                    if (str != null && (str == name || str.EndsWith(name + ".dll", StringComparison.OrdinalIgnoreCase)))
                    { OffAsmImage = off; return img; }
                }
            }
            return 0;
        }

        private bool InMono(long p) { return p >= _monoBase && p < _monoEnd; }

        private static bool Ident(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '`' || c == '<' || c == '>' || c == '$' || c == '.' || c == '=' || c == '{' || c == '}' || c == '-' || c == ',' || c == '|')) return false;
            return true;
        }

        private void LoadClasses()
        {
            // image->class_cache is a MonoInternalHashTable:
            //   { hash_func, key_extract, next_value (3 code pointers into mono), int size, int num_entries, void** table }
            long table = 0; int size = 0;
            for (int off = 0; off < 0x900 && table == 0; off += 8)
            {
                long c = Image + off;
                if (!InMono(M.Ptr(c)) || !InMono(M.Ptr(c + 8)) || !InMono(M.Ptr(c + 16))) continue;
                int sz = M.I32(c + 0x18), ne = M.I32(c + 0x1C);
                long t = M.Ptr(c + 0x20);
                if (sz < 16 || sz > (1 << 22) || ne < 50 || ne > (1 << 22) || t == 0 || M.Bytes(t, 8) == null) continue;
                OffClassCache = off; table = t; size = sz; CacheEntries = ne;
            }
            if (table == 0) throw new InvalidOperationException("class cache not found in the image");

            var raw = M.Bytes(table, size * 8);
            var heads = new List<long>();
            for (int i = 0; i < size; i++) { long h = BitConverter.ToInt64(raw, i * 8); if (h != 0) heads.Add(h); }

            // MonoClass: ... image, name, name_space ... — name is the slot that reads as an
            // identifier while the slot before it is our image.
            int best = 0;
            for (int off = 0x30; off < 0x80; off += 8)
            {
                int ok = 0;
                for (int i = 0; i < heads.Count && i < 200; i++)
                    if (M.Ptr(heads[i] + off - 8) == Image && Ident(M.CStr(M.Ptr(heads[i] + off))) && M.CStr(M.Ptr(heads[i] + off + 8)) != null) ok++;
                if (ok > best) { best = ok; OffName = off; }
            }
            if (best < Math.Min(heads.Count, 200) * 9 / 10) throw new InvalidOperationException("class name slot not found (best " + best + ")");

            // MonoClassDef->next_class_cache chains a bucket. The right slot is the one whose
            // chains visit exactly num_entries valid classes.
            int bestTotal = -1;
            for (int off = 0xA0; off < 0x180; off += 8)
            {
                int total = 0; bool bad = false;
                foreach (long h in heads)
                {
                    int guard = 0;
                    for (long k = h; k != 0; k = M.Ptr(k + off))
                    {
                        if (M.Ptr(k + OffName - 8) != Image || ++guard > 5000) { bad = true; break; }
                        total++;
                    }
                    if (bad) break;
                }
                if (!bad && total > bestTotal) { bestTotal = total; OffNextClass = off; }
                if (!bad && total == CacheEntries) break;
            }
            if (bestTotal < heads.Count) throw new InvalidOperationException("next_class_cache slot not found");

            foreach (long h in heads)
                for (long k = h; k != 0; k = M.Ptr(k + OffNextClass))
                {
                    string name = M.CStr(M.Ptr(k + OffName)), ns = M.CStr(M.Ptr(k + OffName + 8));
                    if (name == null) continue;
                    string full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                    if (!_classes.ContainsKey(full)) _classes[full] = k;
                    ClassesFound++;
                }
        }

        // ── classes, fields, statics ────────────────────────────────────────────────────────────
        public long Class(string name) { long k; return _classes.TryGetValue(name, out k) ? k : 0; }
        public string ClassName(long k) { return M.CStr(M.Ptr(k + OffName)); }
        public long Parent(long k) { return M.Ptr(k + OffName - 0x18); }

        /// <summary>Fields DECLARED by this class (MonoClassField[] = {type, name, parent, offset}, 0x20 each).</summary>
        public List<Field> Fields(long k)
        {
            List<Field> list;
            if (_fields.TryGetValue(k, out list)) return list;
            list = new List<Field>();
            long arr = FieldArray(k);
            for (int i = 0; arr != 0 && i < 1024; i++)
            {
                long f = arr + i * 0x20;
                if (M.Ptr(f + 0x10) != k) break;                       // parent back-pointer: end of our array
                string name = M.CStr(M.Ptr(f + 8));
                if (name == null) break;
                bool isStatic = (M.U16(M.Ptr(f) + 8) & 0x10) != 0;     // MonoType.attrs & FIELD_ATTRIBUTE_STATIC
                list.Add(new Field { Name = name, Offset = M.I32(f + 0x18), Static = isStatic });
            }
            _fields[k] = list;
            return list;
        }

        private long FieldArray(long k)
        {
            if (OffFields != 0) return M.Ptr(k + OffFields);
            for (int off = 0x58; off < 0x100; off += 8)
            {
                long f = M.Ptr(k + off);
                if (f != 0 && M.Ptr(f + 0x10) == k && Ident(M.CStr(M.Ptr(f + 8)))) { OffFields = off; return f; }
            }
            return 0;
        }

        /// <summary>Address of the class's static field block in the root domain, 0 when the class has no vtable yet.</summary>
        public long StaticData(long k)
        {
            long vt = VTable(k);
            if (vt == 0) return 0;
            return M.Ptr(vt + OffVtArray + 8L * M.I32(k + OffVtSize));
        }

        // klass->runtime_info->domain_vtables[0]; MonoVTable starts with its klass.
        private long VTable(long k)
        {
            if (OffRuntimeInfo == 0) return 0;
            long v = M.Ptr(M.Ptr(k + OffRuntimeInfo) + 8);
            return M.Ptr(v) == k ? v : 0;
        }

        /// <summary>
        /// Finds three slots at once, using a singleton class: klass->runtime_info, klass->vtable_size
        /// and the start of MonoVTable's slot array. The static block is vtable[vtable_size], and the
        /// object stored in the class's own static <paramref name="field"/> must be an instance of
        /// that class. The back-pointer test alone (vtable->klass == klass) is NOT enough for
        /// runtime_info: four other slots in MonoClass pass it (seen live on Unity 6000.3.11).
        /// </summary>
        public bool CalibrateStatics(string cls, string field)
        {
            long k = Class(cls); if (k == 0) return false;
            int fo = -1;
            foreach (var f in Fields(k)) if (f.Static && f.Name == field) fo = f.Offset;
            if (fo < 0) return false;
            for (int ro = 0x60; ro < 0x160; ro += 8)
            {
                long vt = M.Ptr(M.Ptr(k + ro) + 8);
                if (vt == 0 || M.Ptr(vt) != k) continue;
                for (int so = 0x40; so < 0x80; so += 4)
                {
                    int n = M.I32(k + so);
                    if (n < 0 || n > 4000) continue;
                    for (int ao = 0x38; ao <= 0x58; ao += 8)
                    {
                        long obj = M.Ptr(M.Ptr(vt + ao + 8L * n) + fo);
                        if (obj != 0 && ClassOf(obj) == k) { OffRuntimeInfo = ro; OffVtSize = so; OffVtArray = ao; return true; }
                    }
                }
            }
            return false;
        }

        // ── objects ─────────────────────────────────────────────────────────────────────────────
        public long ClassOf(long obj) { return M.Ptr(M.Ptr(obj)); }               // obj->vtable->klass

        public long StaticObj(string cls, string field)
        {
            long k = Class(cls); if (k == 0) return 0;
            long sd = StaticData(k); if (sd == 0) return 0;
            foreach (var f in Fields(k)) if (f.Static && f.Name == field) return M.Ptr(sd + f.Offset);
            return 0;
        }

        /// <summary>Address of an instance field, searching the class and its bases. 0 when absent.</summary>
        public long FieldAddr(long obj, string name)
        {
            if (obj == 0) return 0;
            int depth = 0;
            for (long k = ClassOf(obj); k != 0 && depth < 32; k = Parent(k), depth++)
                foreach (var f in Fields(k)) if (!f.Static && f.Name == name) return obj + f.Offset;
            return 0;
        }

        public long Obj(long obj, string field) { long a = FieldAddr(obj, field); return a == 0 ? 0 : M.Ptr(a); }
        public int Int(long obj, string field) { long a = FieldAddr(obj, field); return a == 0 ? 0 : M.I32(a); }

        /// <summary>System.String: int length at 0x10, UTF-16 chars at 0x14.</summary>
        public string Str(long s)
        {
            if (s == 0) return null;
            int len = M.I32(s + 0x10);
            if (len < 0 || len > 4096) return null;
            var b = M.Bytes(s + 0x14, len * 2);
            return b == null ? null : (len == 0 ? "" : Encoding.Unicode.GetString(b));
        }

        public string Str(long obj, string field) { return Str(Obj(obj, field)); }

        // Arrays: element 0 at 0x20, length at 0x18.
        public int ArrayLen(long arr) { return arr == 0 ? 0 : M.I32(arr + 0x18); }

        /// <summary>Blizzard.T5.Core.Map&lt;int, TRef&gt; (old Mono dictionary): a slot is live when its link's HashCode has the top bit set.</summary>
        public IEnumerable<KeyValuePair<int, long>> IntMap(long map)
        {
            long links = Obj(map, "linkSlots"), keys = Obj(map, "keySlots"), vals = Obj(map, "valueSlots");
            int n = Math.Min(ArrayLen(links), 4096);
            for (int i = 0; i < n; i++)
            {
                if ((M.I32(links + 0x20 + i * 8L) & int.MinValue) == 0) continue;
                yield return new KeyValuePair<int, long>(M.I32(keys + 0x20 + i * 4L), M.Ptr(vals + 0x20 + i * 8L));
            }
        }

        public string Describe()
        {
            return "root domain 0x" + RootDomain.ToString("X") + ", " + AssemblyCount + " assemblies; image 0x" + Image.ToString("X")
                + "; classes " + ClassesFound + "/" + CacheEntries + " (" + _classes.Count + " distinct names)\n"
                + "offsets: assembly.image=0x" + OffAsmImage.ToString("X") + " image.class_cache=0x" + OffClassCache.ToString("X")
                + " class.name=0x" + OffName.ToString("X") + " class.next=0x" + OffNextClass.ToString("X")
                + " class.fields=0x" + OffFields.ToString("X") + " class.runtime_info=0x" + OffRuntimeInfo.ToString("X")
                + " class.vtable_size=0x" + OffVtSize.ToString("X") + " vtable.slots=0x" + OffVtArray.ToString("X");
        }

        private static string Hex(byte[] b) { return b == null ? "(unreadable)" : BitConverter.ToString(b); }
    }
}
