# Webcam Analysis MCP — каталог тулов

<!-- GENERATED:ToolCatalog START -->

> Автогенерация из `ToolCatalog.Build()`. Не править этот блок вручную.
>
> Обновление: из каталога проекта выполнить `dotnet run --project tools/ExportMcpManifest -- --write`.
>
> Тексты совпадают с полем `description` у инструментов MCP; полная схема — в `inputSchema`.

### `analyze_burst_sequence`

Проанализировать серию кадров (burst) как квазии-видео: оценить динамику движения, выделить пики/сцены и вернуть структурированный отчёт.

### `pdf_capture_burst`

Сделать серию изображений страниц PDF. Сохраняет страницы как картинки в подпапку внутри workspace и возвращает JSON с путём и параметрами страниц.

### `ocr_image_batch`

Сделать OCR для серии изображений (страницы PDF или burst) через внешний tesseract и вернуть JSON по страницам.

### `analyze_audio_sequence`

Проанализировать WAV-файл: громкость, пики, долю тишины, активность речи/звука и таймлайн по окнам.

### `transcribe_audio_whisper`

Локальная транскрипция аудио через Whisper.net (whisper.cpp runtime). Поддерживаются WAV и WebM/MP4/M4A (через FFmpeg в PATH). Возвращает распознанный текст и сегменты с таймкодами.

### `analyze_av_sequence`

Комплексный анализ A/V-сессии: объединяет динамику движения из кадров и аудио-активность в одном отчёте.

<!-- GENERATED:ToolCatalog END -->

