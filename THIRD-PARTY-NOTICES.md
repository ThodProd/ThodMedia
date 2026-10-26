# Third-party notices — ThodMedia

ThodMedia itself is licensed under the MIT License (see `LICENSE`).
The package bundles the components below. Full license texts are in `licenses/`.

## ffmpeg 9.0.2 essentials build

- Project: https://ffmpeg.org/
- Build: Gyan Doshi, https://www.gyan.dev/ffmpeg/builds/ (essentials, static, win-x64)
- License: GNU General Public License v3 (the build is configured with `--enable-gpl --enable-version3`)
- Binaries in package: `worker/runtime/ffmpeg/ffmpeg.exe`, `worker/runtime/ffmpeg/ffprobe.exe`
- License text: `licenses/ffmpeg-COPYING.txt` (copied from the official build archive)
- Corresponding source: `licenses/FFMPEG-SOURCE-OFFER.txt`

ThodMedia invokes ffmpeg as a separate process through its command-line interface; no ffmpeg code is linked into ThodMedia. Because the bundled build is GPLv3, the package as a whole is distributed under GPLv3 terms. The ffmpeg source code is available from the locations listed in the source offer.

## .NET 10 runtime and Windows Desktop libraries

- Project: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf
- License: MIT
- The self-contained worker includes the official Microsoft runtime files.

## ThodHUB SDK (`ui/thodhub.js`)

- Project: https://github.com/ThodProd/ThodHUB
- License: MIT (part of the ThodHUB project; copied unchanged for the embedded UI bridge)

## Icons

Application icon and artwork are original works of the ThodProd project, MIT.
