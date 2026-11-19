using System;
using System.IO;

namespace TLDCoop
{
    /// <summary>LZF (liblzf) — the compression The Long Dark uses for save files.</summary>
    internal static class Lzf
    {
        public static byte[] Decompress(byte[] input)
        {
            var output = new MemoryStream(input.Length * 3);
            var buf = new byte[Math.Max(16, input.Length * 4)];
            int op = 0, ip = 0, n = input.Length;
            while (ip < n)
            {
                int ctrl = input[ip++];
                if (ctrl < 32)
                {
                    int len = ctrl + 1;
                    Ensure(ref buf, op + len);
                    Buffer.BlockCopy(input, ip, buf, op, len);
                    ip += len; op += len;
                }
                else
                {
                    int len = ctrl >> 5;
                    int reference = op - ((ctrl & 0x1f) << 8) - 1;
                    if (len == 7) len += input[ip++];
                    reference -= input[ip++];
                    len += 2;
                    if (reference < 0) throw new InvalidDataException("LZF: bad back reference");
                    Ensure(ref buf, op + len);
                    for (int i = 0; i < len; i++) buf[op++] = buf[reference++];
                }
            }
            var result = new byte[op];
            Buffer.BlockCopy(buf, 0, result, 0, op);
            return result;
        }

        private static void Ensure(ref byte[] buf, int size)
        {
            if (size <= buf.Length) return;
            int ns = buf.Length * 2;
            while (ns < size) ns *= 2;
            Array.Resize(ref buf, ns);
        }

        private const int HLOG = 16, HSIZE = 1 << HLOG, MAX_LIT = 1 << 5, MAX_OFF = 1 << 13, MAX_REF = (1 << 8) + (1 << 3);

        /// <summary>Port of liblzf's lzf_compress (very fast mode off).</summary>
        public static byte[] Compress(byte[] input)
        {
            int inLen = input.Length;
            var output = new byte[inLen + inLen / 16 + 64];
            var htab = new int[HSIZE];
            for (int i = 0; i < HSIZE; i++) htab[i] = -1;

            int ip = 0, op = 0, lit = 0;
            op++; // start run
            if (inLen == 0) return Array.Empty<byte>();
            int hval = inLen >= 2 ? (input[0] << 8) | input[1] : 0;

            while (ip < inLen - 2)
            {
                hval = (hval << 8) | input[ip + 2];
                int hslot = Hash(hval);
                int reference = htab[hslot];
                htab[hslot] = ip;
                int off;
                if (reference >= 0 && (off = ip - reference - 1) < MAX_OFF
                    && input[reference + 2] == input[ip + 2] && input[reference + 1] == input[ip + 1] && input[reference] == input[ip])
                {
                    int len = 2;
                    int maxlen = inLen - ip - len;
                    if (maxlen > MAX_REF) maxlen = MAX_REF;

                    EnsureOut(ref output, op + 3 + 1);
                    if (lit == 0) op--; // undo run if length is zero
                    else output[op - lit - 1] = (byte)(lit - 1);

                    do len++; while (len < maxlen && input[reference + len] == input[ip + len]);
                    len -= 2;
                    ip++;

                    if (len < 7) output[op++] = (byte)((off >> 8) + (len << 5));
                    else { output[op++] = (byte)((off >> 8) + (7 << 5)); output[op++] = (byte)(len - 7); }
                    output[op++] = (byte)off;

                    lit = 0; op++; // start new run
                    ip += len + 1;
                    if (ip >= inLen - 2) break;

                    --ip;
                    hval = (input[ip] << 8) | input[ip + 1];
                    hval = (hval << 8) | input[ip + 2];
                    htab[Hash(hval)] = ip;
                    ip++;
                    hval = (input[ip] << 8) | input[ip + 1];
                }
                else
                {
                    EnsureOut(ref output, op + 2);
                    lit++; output[op++] = input[ip++];
                    if (lit == MAX_LIT) { output[op - lit - 1] = (byte)(lit - 1); lit = 0; op++; }
                }
            }

            while (ip < inLen)
            {
                EnsureOut(ref output, op + 2);
                lit++; output[op++] = input[ip++];
                if (lit == MAX_LIT) { output[op - lit - 1] = (byte)(lit - 1); lit = 0; op++; }
            }
            output[op - lit - 1] = (byte)(lit - 1);
            if (lit == 0) op--; // end run was empty
            var result = new byte[op];
            Buffer.BlockCopy(output, 0, result, 0, op);
            return result;
        }

        private static int Hash(int v) => (int)((((uint)v >> (3 * 8 - HLOG)) - (uint)v * 5) & (HSIZE - 1));

        private static void EnsureOut(ref byte[] buf, int size)
        {
            if (size <= buf.Length) return;
            Array.Resize(ref buf, Math.Max(size, buf.Length * 2));
        }
    }
}
