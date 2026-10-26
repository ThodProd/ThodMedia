# ThodMedia

Конвертер фото, видео и звука для [ThodHUB](https://github.com/ThodProd/ThodHUB). Работает целиком офлайн: файлы не покидают компьютер.

## Возможности

- **Фото** — PNG, JPEG, WebP, AVIF, BMP, TIFF, GIF и другие форматы через ffmpeg, изменение размера, сжатие, метаданные.
- **Видео** — H.264/H.265/VP9/ProRes/GIF, CRF, разрешение, кадры/с, точная или быстрая обрезка, удаление звука.
- **Звук** — извлечение дорожки в MP3, WAV, FLAC, AAC, Opus или без перекодирования; обрезка.
- **Дорожка** — замена, добавление второго трека или сведение с исходным звуком.
- Редактор изображений с удалением фона вынесен в отдельный [ThodCanvas](https://github.com/ThodProd/ThodCanvas): слои, кисти, выделения и исходное разрешение.
- **Пакетная обработка** — выберите несколько файлов, результат складывается в подпапку `ThodMedia` рядом с исходниками или в указанную папку.

Исходные файлы не изменяются, если не включён «Заменять». По умолчанию рядом с исходником создаётся папка `ThodMedia`.

## Установка

1. Откройте ThodHUB → «Магазин».
2. Найдите **ThodMedia** и нажмите «Установить».
3. Запустите программу из библиотеки хаба.

Вручную: скачайте ZIP из [Releases](https://github.com/ThodProd/ThodMedia/releases), распакуйте в отдельную папку и установите через ThodHUB (пакет содержит `manifest.json` в корне).

Требования: Windows 10/11 x64, ThodHUB 0.1.0+. Отдельный .NET Runtime не нужен — worker публикуется self-contained.

## Разработка

```
src/ThodMedia.Worker   — .NET 10 worker, JSON-RPC 2.0 поверх stdin/stdout
ui                     — HTML/CSS/JS интерфейс (ThodHUB SDK)
hub/manifest.json      — манифест модуля
scripts/build.ps1      — сборка, загрузка инструментов и упаковка
```

Сборка:

```powershell
.\scripts\build.ps1 -FfmpegZip .\ffmpeg-9.0.2-essentials_build.zip
```

Без параметров скрипт скачает закреплённые версии ffmpeg в `.cache` и проверит SHA-256. Результат — `artifacts/thodmedia-<version>-win-x64.zip` и `artifacts/thodhub-release.json`.

Проверка worker без хаба:

```powershell
node tools/rpc-test.mjs "src/ThodMedia.Worker/bin/Release/net10.0-windows/ThodMedia.Worker.exe"
```

## Лицензии

Код ThodMedia — MIT. В пакет входят ffmpeg (GPLv3), ONNX Runtime (MIT) и .NET (MIT). Подробности и ссылки на исходники — в [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) и папке `licenses` внутри пакета. Пакет с ffmpeg распространяется на условиях GPLv3; исходники ffmpeg доступны по ссылке из уведомлений.
