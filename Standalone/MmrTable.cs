using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace Toolkit.Standalone
{
    /// <summary>
    /// The site's per-region leaderboard blob, <c>https://hsbg.cards/bgmmr/{REGION}.json</c> — the same
    /// file the HDT plugin reads. Fetched once per region; a failed fetch retries after 60 s.
    /// </summary>
    internal sealed class MmrTable
    {
        [DataContract]
        private sealed class Blob
        {
            [DataMember(Name = "players")] public Dictionary<string, int> Players { get; set; }
        }

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private readonly Action<string> _log;
        private string _region, _loading;
        private DateTime _lastFail = DateTime.MinValue;
        private volatile Dictionary<string, int> _exact, _ci;

        public MmrTable(Action<string> log) { _log = log; }

        /// <summary>Starts loading the region's table if it isn't loaded or loading.</summary>
        public void Ensure(string region)
        {
            if (region == null || region == _region || region == _loading) return;
            if ((DateTime.UtcNow - _lastFail).TotalSeconds < 60) return;
            _loading = region;
            Task.Run(() => Load(region));
        }

        private async Task Load(string region)
        {
            try
            {
                var json = await Http.GetStringAsync("https://hsbg.cards/bgmmr/" + region + ".json");
                var ser = new DataContractJsonSerializer(typeof(Blob), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
                Blob blob;
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json))) blob = (Blob)ser.ReadObject(ms);
                var exact = new Dictionary<string, int>(blob.Players, StringComparer.Ordinal);
                var ci = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in exact) { int cur; if (!ci.TryGetValue(kv.Key, out cur) || kv.Value > cur) ci[kv.Key] = kv.Value; }
                _exact = exact; _ci = ci; _region = region;
                _log("MMR table " + region + ": " + exact.Count + " players");
            }
            catch (Exception ex)
            {
                _lastFail = DateTime.UtcNow;
                _log("MMR table " + region + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { _loading = null; }
        }

        /// <summary>The rating for a display name; null when the table isn't loaded or the name isn't on it.</summary>
        public int? Lookup(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var exact = _exact; var ci = _ci;
            if (exact == null) return null;
            int r;
            if (exact.TryGetValue(name, out r) || ci.TryGetValue(name, out r)) return r;
            return null;
        }

        public bool Loaded => _exact != null;
    }
}
