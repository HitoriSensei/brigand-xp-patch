// Finds WAV files that DirectSound on Windows XP may refuse and converts them
// to plain 16-bit PCM in place, backing up the originals.
// Built against .NET Framework 2.0, so only 2.0 APIs are used.
// Based on:
// - https://github.com/naudio/NAudio (MIT)
// - https://sourceforge.net/projects/sox/ (LGPL)
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

[assembly: AssemblyTitle("wavscan")]
[assembly: AssemblyDescription("Converts WAV files DirectSound on Windows XP may refuse to plain 16-bit PCM")]
[assembly: AssemblyCopyright("Copyright 2026 Hitori, CC BY-NC 4.0")]
[assembly: AssemblyVersion("1.0.0.0")]

static class WavScan
{
    const ushort PCM = 1, FLOAT = 3, EXTENSIBLE = 0xFFFE;

    const string Usage =
        "Finds WAV files that DirectSound on Windows XP may refuse and converts them\r\n" +
        "to plain 16-bit PCM in place. The originals are moved to the backup directory,\r\n" +
        "in the same folder structure.\r\n\r\n" +
        "Usage:\r\n" +
        "  wavscan <assets dir>                report only\r\n" +
        "  wavscan <assets dir> <backup dir>   back up and convert\r\n\r\n" +
        "Dropping a folder on wavscan.exe converts it, with the backup in <folder>_backup.\r\n" +
        "Channel count and sample rate are kept. Stereo files are only reported.";

    class Format
    {
        public ushort Tag, Sub, Channels, BlockAlign, Bits;
        public uint Rate;
        public long DataOffset, DataSize = -1;

        public ushort Effective { get { return Tag == EXTENSIBLE ? Sub : Tag; } }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetConsoleProcessList(uint[] list, uint count);

    // When the console has no other process attached, we were started from
    // Explorer (double click or drag and drop), so the window would close
    // before anyone reads it.
    static bool OwnConsole()
    {
        try { return GetConsoleProcessList(new uint[4], 4) == 1; }
        catch (Exception) { return false; }
    }

    static int Main(string[] args)
    {
        bool own = OwnConsole();
        int rc = Run(args, own);
        if (own)
        {
            Console.WriteLine();
            Console.Write("Press Enter to close.");
            Console.ReadLine();
        }
        return rc;
    }

    static int Run(string[] args, bool own)
    {
        if (args.Length < 1 || args.Length > 2)
        {
            Console.WriteLine(Usage);
            return 1;
        }
        string root = Path.GetFullPath(args[0]).TrimEnd('\\', '/');
        if (!Directory.Exists(root))
        {
            Console.WriteLine("Not a directory: " + root);
            return 1;
        }
        string backupRoot = null;
        if (args.Length == 2) backupRoot = Path.GetFullPath(args[1]).TrimEnd('\\', '/');
        else if (own) backupRoot = root + "_backup";
        if (backupRoot != null &&
            (string.Compare(backupRoot, root, StringComparison.OrdinalIgnoreCase) == 0 ||
             backupRoot.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("Backup directory must be outside the assets directory.");
            return 1;
        }
        if (backupRoot != null) Console.WriteLine("Backing up original files to " + backupRoot);

        int found = 0, converted = 0, failed = 0;
        foreach (string path in WavFiles(root))
        {
            string rel = path.Substring(root.Length + 1);
            Format f;
            string err = ReadFormat(path, out f);
            if (err != null)
            {
                Console.WriteLine("BAD   " + rel + ": " + err);
                found++;
                continue;
            }
            List<string> fix = new List<string>(), warn = new List<string>();
            Check(f, fix, warn);
            if (warn.Count > 0)
                Console.WriteLine("WARN  " + rel + ": " + Describe(f) + ": " + string.Join("; ", warn.ToArray()));
            if (fix.Count == 0) continue;
            found++;
            Console.WriteLine("BAD   " + rel + ": " + Describe(f) + ": " + string.Join("; ", fix.ToArray()));
            if (backupRoot == null) continue;
            string problem;
            try { problem = ConvertInPlace(path, f, Path.Combine(backupRoot, rel)); }
            catch (Exception e) { problem = e.Message; }
            if (problem != null)
            {
                Console.WriteLine("FAIL  " + rel + ": " + problem);
                failed++;
            }
            else converted++;
        }

        Console.Write(found + " file(s) need conversion");
        Console.WriteLine(backupRoot != null ? ", " + converted + " converted, " + failed + " failed" : "");
        return failed > 0 ? 2 : 0;
    }

    static IEnumerable<string> WavFiles(string dir)
    {
        string[] files = Directory.GetFiles(dir);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (string f in files)
            if (f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) yield return f;
        string[] dirs = Directory.GetDirectories(dir);
        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
        foreach (string d in dirs)
            foreach (string f in WavFiles(d)) yield return f;
    }

    static string ReadFormat(string path, out Format f)
    {
        f = null;
        using (FileStream s = File.OpenRead(path))
        {
            // The chunk walk follows NAudio's WaveFileChunkReader.ReadWaveHeader (MIT, see LICENSE.md):
            // https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Core/FileFormats/Wav/WaveFileChunkReader.cs
            BinaryReader r = new BinaryReader(s);
            if (s.Length < 12 || Ascii(r.ReadBytes(4)) != "RIFF") return "not a RIFF/WAVE file";
            long riffSize = r.ReadUInt32();
            if (Ascii(r.ReadBytes(4)) != "WAVE") return "not a RIFF/WAVE file";
            Format fmt = null;
            long dataOffset = -1, dataSize = 0;
            // Some files have more data than the RIFF header says, so we stop at whichever ends first
            long stop = Math.Min(riffSize + 8, s.Length);
            while (s.Position <= stop - 8)
            {
                string id = Ascii(r.ReadBytes(4));
                long size = r.ReadUInt32();
                long next = s.Position + size;
                if (id == "data")
                {
                    // The data chunk can come before fmt, so let's only note where it is
                    dataOffset = s.Position;
                    dataSize = Math.Min(size, s.Length - s.Position);
                }
                else if (id != "fmt " && size > s.Length - s.Position)
                {
                    // A broken chunk after the ones we need, so let's keep what we have
                    break;
                }
                else if (id == "fmt ")
                {
                    if (size < 16) return "fmt chunk too short";
                    fmt = new Format();
                    fmt.Tag = r.ReadUInt16();
                    fmt.Channels = r.ReadUInt16();
                    fmt.Rate = r.ReadUInt32();
                    r.ReadUInt32();
                    fmt.BlockAlign = r.ReadUInt16();
                    fmt.Bits = r.ReadUInt16();
                    if (fmt.Tag == EXTENSIBLE && size >= 26)
                    {
                        // WAVEFORMATEXTENSIBLE puts cbSize, wValidBitsPerSample and dwChannelMask here,
                        // and the first two bytes of the SubFormat GUID are the old format tag.
                        // https://learn.microsoft.com/en-us/windows/win32/api/mmreg/ns-mmreg-waveformatextensible
                        r.ReadBytes(8);
                        fmt.Sub = r.ReadUInt16();
                    }
                }
                s.Position = next;
                // An odd sized chunk is followed by a zero pad byte, but not every writer adds it,
                // so like NAudio we skip it only when it's there
                if ((size & 1) != 0 && s.Position < s.Length)
                {
                    if (s.ReadByte() != 0) s.Position--;
                }
            }
            if (fmt == null) return "no fmt chunk";
            if (dataOffset < 0) return "no data chunk";
            fmt.DataOffset = dataOffset;
            fmt.DataSize = dataSize;
            f = fmt;
            return null;
        }
    }

    static string Ascii(byte[] b) { return Encoding.ASCII.GetString(b); }

    static string TagName(ushort t)
    {
        switch (t)
        {
            case 1: return "PCM";
            case 2: return "MS ADPCM";
            case 3: return "IEEE float";
            case 6: return "A-law";
            case 7: return "mu-law";
            case 0x11: return "IMA ADPCM";
            case 0x55: return "MP3";
            case EXTENSIBLE: return "EXTENSIBLE";
            default: return "0x" + t.ToString("x");
        }
    }

    static string Describe(Format f)
    {
        string d = TagName(f.Tag) + " " + f.Channels + "ch " + f.Rate + "Hz " + f.Bits + "bit";
        if (f.Tag == EXTENSIBLE) d += " (sub=" + TagName(f.Sub) + ")";
        return d;
    }

    static void Check(Format f, List<string> fix, List<string> warn)
    {
        if (f.Tag == EXTENSIBLE) fix.Add("WAVE_FORMAT_EXTENSIBLE header");
        else if (f.Tag != PCM) fix.Add("not plain PCM");
        if (f.Bits != 8 && f.Bits != 16) fix.Add(f.Bits + "-bit samples");
        if (f.Channels != 1) warn.Add("stereo/multichannel (fails as a 3D sound)");
        if (f.Rate < 100 || f.Rate > 100000) warn.Add("sample rate outside DirectSound limits");
    }

    static string ConvertInPlace(string path, Format f, string backup)
    {
        // The original is only replaced once the converted file is complete
        // and checked, so a failure leaves it untouched.
        string tmp = path + ".tmp";
        string problem;
        try { problem = Convert(path, f, tmp); }
        catch (Exception) { File.Delete(tmp); throw; }
        if (problem != null)
        {
            File.Delete(tmp);
            return problem;
        }
        // An existing backup is the real original from an earlier run, so it's kept
        if (!File.Exists(backup))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup));
            File.Copy(path, backup);
        }
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
        File.Move(tmp, path);
        return null;
    }

    // The decoders are NAudio's sample providers (MIT, see LICENSE.md), with double instead of
    // float, as a float can't hold every 32-bit sample exactly.
    // https://github.com/naudio/NAudio/tree/v2.2.1/NAudio.Core/Wave/SampleProviders
    static double DecodePcm(byte[] b, int n, int width)
    {
        switch (width)
        {
            // Pcm8BitToSampleProvider
            case 1: return b[n] / 128.0 - 1.0;
            // Pcm16BitToSampleProvider
            case 2: return BitConverter.ToInt16(b, n) / 32768.0;
            // Pcm24BitToSampleProvider, the (sbyte) cast on the top byte does the sign extension
            case 3: return (((sbyte)b[n + 2] << 16) | (b[n + 1] << 8) | b[n]) / 8388608.0;
            // Pcm32BitToSampleProvider
            default: return (((sbyte)b[n + 3] << 24 | b[n + 2] << 16) | (b[n + 1] << 8) | b[n]) / 2147483648.0;
        }
    }

    // WaveToSampleProvider and WaveToSampleProvider64
    static double DecodeFloat(byte[] b, int n, int width)
    {
        return width == 4 ? BitConverter.ToSingle(b, n) : BitConverter.Int64BitsToDouble(BitConverter.ToInt64(b, n));
    }

    // Convert like sox does
    static string Convert(string src, Format f, string dst)
    {
        ushort kind = f.Effective;
        // The container size comes from the block size, as some writers put the valid bits in wBitsPerSample
        int width = f.Channels > 0 ? f.BlockAlign / f.Channels : 0;
        bool intOk = kind == PCM && (width == 1 || width == 2 || width == 3 || width == 4);
        bool floatOk = kind == FLOAT && (width == 4 || width == 8);
        if (!intOk && !floatOk) return "can't convert " + TagName(kind) + " " + f.Bits + "-bit, convert it manually";

        byte[] data;
        using (FileStream s = File.OpenRead(src))
        {
            s.Position = f.DataOffset;
            data = new BinaryReader(s).ReadBytes((int)f.DataSize);
        }
        int count = data.Length / width;
        short[] pcm = new short[count];

        // Everything goes to the -1.0 to 1.0 range first, so all source formats share one path from here
        double[] x = new double[count];
        double peak = 0;
        for (int i = 0, o = 0; i < count; i++, o += width)
        {
            double v = kind == FLOAT ? DecodeFloat(data, o, width) : DecodePcm(data, o, width);
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            x[i] = v;
            peak = Math.Max(peak, Math.Abs(v));
        }

        if (kind == PCM && width <= 2)
        {
            // 8 and 16-bit samples fit 16 bits exactly (8-bit 255 becomes 32512), so no dither here
            for (int i = 0; i < count; i++)
                pcm[i] = (short)Math.Round(x[i] * 32768.0);
        }
        else
        {
            // Float files can go above full scale, so like sox -G we lower the level only when
            // the result would clip, and keep one LSB of room for the dither below.
            double gain = 32768.0;
            if (peak * gain > 32766) gain = 32766 / peak;
            // Rounding to 16 bits alone gives distortion audible in quiet parts and fades. 
            // TPDF dither turns it into steady white noise, and is what sox adds by
            // default when it reduces bit depth.
            Random rnd = new Random(1);
            for (int i = 0; i < count; i++)
            {
                double v = Math.Floor(x[i] * gain + rnd.NextDouble() - rnd.NextDouble() + 0.5);
                pcm[i] = (short)Math.Max(-32768, Math.Min(32767, v));
            }
        }

        int channels = f.Channels, bytes = count * 2;
        using (BinaryWriter w = new BinaryWriter(File.Create(dst)))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + bytes + (bytes & 1));
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write(PCM);
            w.Write((ushort)channels);
            w.Write(f.Rate);
            w.Write(f.Rate * (uint)channels * 2);
            w.Write((ushort)(channels * 2));
            w.Write((ushort)16);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(bytes);
            foreach (short v in pcm) w.Write(v);
            if ((bytes & 1) != 0) w.Write((byte)0);
        }

        Format check;
        string err = ReadFormat(dst, out check);
        if (err != null) return "output unreadable: " + err;
        List<string> fix = new List<string>(), warn = new List<string>();
        Check(check, fix, warn);
        return fix.Count > 0 ? string.Join("; ", fix.ToArray()) : null;
    }
}
