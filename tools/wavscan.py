#!/usr/bin/env python3
"""Find WAV files that DirectSound on Windows XP may refuse, and convert them
to plain 16-bit PCM with sox, mirroring the folder structure.

Usage:
  python3 wavscan.py <assets dir>                 report only
  python3 wavscan.py <assets dir> <output dir>    report and convert

Channel count and sample rate are kept. Stereo files are only reported:
LOAD 3DSOUND rejects them on every Windows version, so any stereo file that
already works on Windows 10 is loaded as a 2D sound.
"""
import os
import shutil
import struct
import subprocess
import sys

TAGS = {1: "PCM", 2: "MS ADPCM", 3: "IEEE float", 6: "A-law", 7: "mu-law",
        0x11: "IMA ADPCM", 0x55: "MP3", 0xFFFE: "EXTENSIBLE"}


def read_fmt(path):
    with open(path, "rb") as f:
        head = f.read(12)
        if len(head) < 12 or head[:4] != b"RIFF" or head[8:12] != b"WAVE":
            return None, "not a RIFF/WAVE file"
        while True:
            ch = f.read(8)
            if len(ch) < 8:
                return None, "no fmt chunk"
            cid, size = ch[:4], struct.unpack("<I", ch[4:])[0]
            if cid == b"fmt ":
                return f.read(size), None
            f.seek(size + (size & 1), 1)


def check(fmt):
    """Return (description, issues that conversion fixes, warnings)."""
    tag, ch, rate, _, _, bits = struct.unpack_from("<HHIIHH", fmt)
    desc = f"{TAGS.get(tag, hex(tag))} {ch}ch {rate}Hz {bits}bit"
    fix, warn = [], []
    if tag == 0xFFFE:
        if len(fmt) >= 26:
            sub = struct.unpack_from("<H", fmt, 24)[0]
            desc += f" (sub={TAGS.get(sub, hex(sub))})"
        fix.append("WAVE_FORMAT_EXTENSIBLE header")
    elif tag != 1:
        fix.append("not plain PCM")
    if bits not in (8, 16):
        fix.append(f"{bits}-bit samples")
    if ch != 1:
        warn.append("stereo/multichannel (fails as a 3D sound)")
    if not 100 <= rate <= 100000:
        warn.append("sample rate outside DirectSound limits")
    return desc, fix, warn


def convert(sox, src, dst):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    # -G avoids clipping when the source peaks above full scale (float files).
    # -t wavpcm writes a plain WAVE_FORMAT_PCM header.
    cmd = [sox, "-G", src, "-t", "wavpcm", "-b", "16", "-e", "signed-integer", dst]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        return r.stderr.strip() or f"sox exited with {r.returncode}"
    fmt, err = read_fmt(dst)
    if err:
        return f"output unreadable: {err}"
    _, fix, _ = check(fmt)
    return "; ".join(fix) if fix else None


def main():
    if len(sys.argv) not in (2, 3):
        sys.exit(__doc__)
    root = os.path.abspath(sys.argv[1])
    out = os.path.abspath(sys.argv[2]) if len(sys.argv) == 3 else None
    sox = None
    if out:
        if out == root or out.startswith(root + os.sep):
            sys.exit("Output directory must be outside the assets directory.")
        sox = shutil.which("sox")
        if not sox:
            sys.exit("sox not found on PATH.")

    found = converted = failed = 0
    for dirpath, dirs, files in os.walk(root):
        dirs.sort()
        for name in sorted(files):
            if not name.lower().endswith(".wav"):
                continue
            path = os.path.join(dirpath, name)
            rel = os.path.relpath(path, root)
            fmt, err = read_fmt(path)
            if err:
                print(f"BAD   {rel}: {err}")
                found += 1
                continue
            desc, fix, warn = check(fmt)
            if warn:
                print(f"WARN  {rel}: {desc}: {'; '.join(warn)}")
            if not fix:
                continue
            found += 1
            print(f"BAD   {rel}: {desc}: {'; '.join(fix)}")
            if out:
                problem = convert(sox, path, os.path.join(out, rel))
                if problem:
                    print(f"FAIL  {rel}: {problem}")
                    failed += 1
                else:
                    converted += 1

    print(f"{found} file(s) need conversion", end="")
    print(f", {converted} converted, {failed} failed" if out else "")


if __name__ == "__main__":
    main()
