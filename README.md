# Brigand patch for Windows XP

A patch for `Brigand NoPlugin.exe` that makes it playable (fixes crashes) on 32-bit Windows XP with SP2 or later with 3D Hardware accelerated sound and improves the sound quality.

- Fixes "Runtime Error 3004 - Sound does not exist at line 18322" after a few minutes of play (together with the converted WAV files)
- Raises the DirectSound output from 22050 Hz to 48000 Hz, so most sound effects are mixed without resampling
- Gives every sound buffer a hardware voice only while it plays, so hardware 3D sound doesn't run out of voices and fall back to software after a few minutes
- Reuses the sound slot of an effect already loaded, so the game keeps at most one buffer per sound file

With the patch and the converted WAV files the game doesn't crash with hardware acceleration set to "Full" or "Basic".

## Applying

The patch is made against the `Brigand NoPlugin.exe` from version **18.9**:

```
Brigand NoPlugin.exe  13807806 bytes
CRC32  e724110c
SHA-1  3223f7b056da9c2f9681c8aef2838ee9fb8b8baa
```

- Apply `Brigand NoPlugin XP.bps` with https://www.marcrobledo.com/RomPatcher.js/, Floating IPS (flips) or other BPS-capable patcher. The BPS file checks the CRC32 of the original and of the result, so it refuses a wrong file.
  - `Brigand NoPlugin XP.ips` has the same changes, but IPS checks nothing, so use it only if your patcher can't do BPS.
- The patched file must be named `Brigand NoPlugin.exe`, as the launcher looks for that name.
  The result should be CRC32 `08c4fca8`, SHA-1 `759ed1229251d6c4655b7906403e7c5fdfe31df2`.
- Delete `d3d9.dll` from the game folder if it's there. It's the 64-bit Direct3D 9 runtime of Windows 10, which XP can't load, and the game uses the system one anyway.
- Install the DirectX End-User Runtime (June 2010). The game needs `d3dx9_31.dll` and `d3dx9_35.dll`.
- Convert the WAV files. **Without this, the game still may crash or won't play sounds**, as XP's software sound mixing can't create 24-bit buffers. On any machine with Python 3 and https://sourceforge.net/projects/sox/ (`sox` command should be available for the script):

  ```
  python3 tools/wavscan.py "path\to\Brigand\assets" fixed
  ```

  Then copy the contents of `fixed` over `assets`, overwriting files. Only bit depth and header format change, the sample rate and channel count stay the same. Stereo files, if any, are listed as `WARN` and left alone, as the game loads them as 2D sounds.

## How it was fixed

### Runtime error 3004

`Dark Basic Pro SDK/Shared/Error/CRuntimeErrors.h` says 3004 is `RUNTIMEERROR_SOUNDNOTEXIST`. Lines 18306 to 18326 are the only code that loads sounds during play, and decompiled they are:

```
for id = 11 to 500
  if soundName$(id) = file$
    if sound playing(id) = 0 then exit
  else
    if sound exist(id) = 0
      load 3dsound file$, id, 1
      soundName$(id) = file$
      exit
    endif
  endif
next id
set sound volume id, ...
position sound id, ...
play sound id
```

The last parameter of `load 3dsound` is `iSilentFail` (`Shared/Sound/CSoundC.cpp`, `LoadRawSoundCore`), so a failed load raises no error, the slot stays empty, its name is stored anyway, and `set sound volume` on the next line raises 3004.

The load fails because of the WAV files, most of them are 24-bit. With hardware acceleration the sound card takes 24-bit buffers without complaining, but XP's software mixing refuses them, and the game fails straight at the first sound with acceleration set to "Basic" or "None". DBPro creates buffers without a location flag, so DirectSound places each one in hardware when it's created and keeps the hardware voice for as long as the buffer exists, even when it's silent. The game never deletes sounds during play, so after a few minutes the card runs out of 3D voices, the next buffer goes to software, the 24-bit file can't be loaded there, and the game crashes. Windows 10 has no hardware buffers at all and its software mixing accepts 24-bit, which is why it never crashes there.

Two patches deal with that. The first one is in `DBProSoundDebug.dll`, where `LoadRawSoundCore` picks the buffer flags:

```cpp
DWORD dwFlags=DSBCAPS_CTRLPAN | DSBCAPS_CTRLVOLUME | DSBCAPS_CTRLFREQUENCY;
if(b3DSound)
{
    guid3DAlgorithm = DS3DALG_DEFAULT;
    dwFlags = DSBCAPS_CTRL3D | DSBCAPS_CTRLVOLUME | DSBCAPS_CTRLFREQUENCY;
}
```

Both flag sets get `DSBCAPS_LOCDEFER`, which makes DirectSound pick hardware or software only when a sound starts playing, and as far as I can tell from the DirectSound docs, a deferred buffer gives its hardware voice back when it stops, so only the sounds actually playing take hardware voices.

The second one is in the game code, line 18308, where `sound playing(id) = 0` is made always true, so the loop always reuses the slot of the same file and the game needs one buffer per sound file instead of one per sound playing at the same time. The downside is that two copies of the same effect played at once cut each other off instead of playing together. This patch wasn't the fix for the crash (the slots running out was the first guess, before the 24-bit files turned up), but it keeps the number of buffers low, so I've left it in.

### The sound quality

On XP the sound was garbled and muffled. `CSoundC.cpp`, `Constructor` sets up DirectSound with:

```cpp
HRESULT hr = g_pSoundManager->Initialize( m_hWnd, DSSCL_PRIORITY, 2, 22050, 16 );
```

which sets the primary buffer to 22050 Hz stereo, 16-bit. Vista and later ignore the primary buffer format, but XP mixes everything at that rate, so the 48 kHz effects and the 32 kHz and 22 kHz music got resampled down in 16-bit integer. The patch changes the rate to 48000, as most samples are 48 kHz and XP-era sound cards run at 48 kHz anyway.

## Credit notes

Decompilation for runtime error 3004 investigation and compiled byte patch on retail exe was made with the Claude Opus.