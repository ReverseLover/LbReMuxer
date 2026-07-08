# LbReMuxer

A small command-line utility that produces WebM files compatible with the
demuxer used by the international Steam release of the visual novel
**Little Busters! English Edition**.

Little Busters ships with a hand-coded video demuxer that is extremely picky
about file structure. Rather than parsing WebM/Matroska properly, it expects
the *exact* header layout produced by the specific muxer used for the game's
original videos **webmmux 1.0.4.1**. Files produced by any modern encoder
(a current build of `ffmpeg`, `mkvmerge`, etc.) use a slightly different but
perfectly valid structure, and the game refuses to play them.

Re-muxing with the original webmmux 1.0.4.1 is impractical, so this tool takes
a different approach: it uses a known-good header extracted from one of the
game's original videos as a **template**, then copies the essential metadata
and the actual audio/video data from your new file into that template. The
result is a file with a byte layout the game's demuxer accepts, but containing
your video.

## How it works

1. A known-good header from an original game video is embedded in the
   executable (`good_template.bin`) and used as the structural template.
2. The template's `Segment` is rebuilt:
   - `SeekHead`, `Cues`, and `Void` elements are dropped (their offsets would
     be stale).
   - `Info` fields (`TimestampScale`, `Duration`) are patched from the source.
   - `Tracks` are matched to the source by track type, and the fields that
     describe the content pixel dimensions, channel count, sampling rate,
     and `CodecPrivate` are patched in from the source.
   - All media `Cluster` elements are copied verbatim from the source.
3. The template's `EBML` header is prepended unchanged and the file is written
   out.

## Requirements

The source file must be a WebM video with:

- a single **VP8** video track, and
- a single **Vorbis** audio track.

This matches the format the game uses. Other codecs or track layouts are not
supported.

## Usage

```
LbReMuxer <source.webm> <output.webm>
```

Example:

```
LbReMuxer my_new_video.webm patched_video.webm
```

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build -c Release
```

## License

[MIT](LICENSE)