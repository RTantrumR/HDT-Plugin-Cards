using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MemSpike
{
    /// <summary>Read-only view of another process's memory. Every read fails soft (0 / null).</summary>
    internal sealed class Mem : IDisposable
    {
        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
        [DllImport("kernel32")]
        private static extern bool CloseHandle(IntPtr h);

        private const uint PROCESS_VM_READ = 0x0010, PROCESS_QUERY_INFORMATION = 0x0400;
        private readonly IntPtr _h;
        private readonly byte[] _b8 = new byte[8];
        public long Reads;

        public Mem(int pid)
        {
            _h = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
            if (_h == IntPtr.Zero) throw new InvalidOperationException("OpenProcess failed, error " + Marshal.GetLastWin32Error());
        }

        public void Dispose() { CloseHandle(_h); }

        public bool TryRead(long addr, byte[] buf, int n)
        {
            if (addr < 0x10000 || addr > 0x7FFFFFFFFFFF) return false;
            Reads++;
            IntPtr got;
            return ReadProcessMemory(_h, (IntPtr)addr, buf, (IntPtr)n, out got) && (long)got == n;
        }

        public byte[] Bytes(long addr, int n)
        {
            if (n <= 0 || n > (64 << 20)) return null;
            var b = new byte[n];
            return TryRead(addr, b, n) ? b : null;
        }

        public long Ptr(long addr) { return TryRead(addr, _b8, 8) ? BitConverter.ToInt64(_b8, 0) : 0; }
        public int I32(long addr) { return TryRead(addr, _b8, 4) ? BitConverter.ToInt32(_b8, 0) : 0; }
        public int U16(long addr) { return TryRead(addr, _b8, 2) ? BitConverter.ToUInt16(_b8, 0) : 0; }

        /// <summary>NUL-terminated printable ASCII string, or null when the bytes are not one.</summary>
        public string CStr(long addr, int max = 200)
        {
            if (addr < 0x10000) return null;
            var sb = new StringBuilder();
            var chunk = new byte[64];
            while (sb.Length < max)
            {
                int n = (int)Math.Min(64, 4096 - (addr & 0xFFF));   // never cross a page in one read
                if (!TryRead(addr, chunk, n)) return null;
                for (int i = 0; i < n; i++)
                {
                    byte c = chunk[i];
                    if (c == 0) return sb.ToString();
                    if (c < 0x20 || c > 0x7E) return null;
                    sb.Append((char)c);
                }
                addr += n;
            }
            return null;
        }
    }
}
