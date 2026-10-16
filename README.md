# Convert-Z

Right-click a video, audio or image file, pick **Convert to** and a format, and the converted file shows
up next to the original. No windows pop up. A small tray icon shows the progress and goes away when it's
done.

It's a front end for ffmpeg. On NVIDIA cards, video is decoded and encoded on the GPU (NVENC). Without
one it falls back to the CPU.

## Install

1. Download `ConvertZ.zip` from [Releases](../../releases) and unzip it.
2. Run `Install.cmd`.

It installs for your user only, so no admin is needed. If you don't have ffmpeg, it gets it with winget.
`Uninstall.cmd` removes everything again.

On Windows 11's new right-click menu, the entry is under **Show more options**.

## Formats

| Right-click on | Convert to |
|---|---|
| mp4 mkv mov avi wmv flv webm m4v mpg mpeg ts mts m2ts 3gp ogv vob | MP4, MKV, WebM, MOV, AVI, GIF, and MP3, M4A, WAV, FLAC (audio only) |
| gif | MP4, WebM, MOV, MKV, PNG |
| mp3 wav flac m4a m4b aac ogg oga opus wma aif aiff ac3 amr mka wv ape | MP3, M4A, WAV, FLAC, OGG, Opus |
| jpg jpeg jfif png bmp webp tif tiff ico tga avif jxl | PNG, JPG, WebP, BMP, TIFF, ICO |

## Details

- **Remux first.** If the streams already fit the new container, they're copied instead of re-encoded,
  so an OBS `.mkv` turns into an `.mp4` in seconds, with every audio track kept.
- **Codecs.** When it does re-encode, MP4, MOV and MKV get H.264 and WebM gets AV1.
- **NVIDIA driver.** GPU encoding with ffmpeg 8.1 needs NVIDIA driver 610 or newer.
- **Nothing gets overwritten.** If the name is taken, you get `name (1).mp4`.
- **Multiple files.** Select several and they queue behind one tray icon. Right-click the icon to cancel.
- **Errors.** If something fails, one dialog says why at the end. The log is in
  `%LocalAppData%\ConvertZ\ConvertZ.log`.

## Is the download safe?

- **Built by GitHub.** The release zip is built and tested by [GitHub Actions](.github/workflows/release.yml)
  from the tagged source, not uploaded from someone's PC.
- **Verifiable.** Each release has a signed build attestation and SHA-256 checksums. To check that a
  download came from this repo's workflow:
  ```
  gh attestation verify ConvertZ.zip --repo MRZ-OW/Convert-Z
  ```
- **Small enough to read.** The exe is a small .NET Framework program, so you can open it in ILSpy and
  read it, or build it yourself.
- **Not code-signed.** Windows may warn that the publisher is unknown.

## Building

You need the .NET SDK. It targets .NET Framework 4.8, which comes with Windows 10 and 11, and needs
ffmpeg on PATH for the tests.

```
.\build.ps1 -SelfTest
```

The self-test converts generated clips through every menu entry and checks each result with ffprobe.
With an NVIDIA card, it also checks that video re-encodes really ran on the GPU. `e2e.ps1` clicks the
installed menu through the Windows shell.

## License

[MIT](LICENSE)
