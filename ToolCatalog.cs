using System.Text.Json;
using ModelContextProtocol.Protocol;
using Tool = ModelContextProtocol.Protocol.Tool;

namespace WebcamAnalysisMcp;

/// <summary>Каталог MCP-тулов. Согласован с <c>mcp-tools.manifest.json</c> и <c>docs/MCP-TOOLS.md</c> (генерация: <c>tools/ExportMcpManifest</c>).</summary>
internal static class ToolCatalog
{
    private static JsonElement Schema(object schema) => JsonSerializer.SerializeToElement(schema);

    internal static List<Tool> Build() =>
    [
        new()
        {
            Name = "analyze_burst_sequence",
        Description = "Проанализировать серию кадров (burst) как квазии-видео: оценить динамику движения, выделить пики/сцены и вернуть структурированный отчёт.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                burst_dir = new { type = "string", description = "Путь к папке burst (абсолютный или относительный к workspace)." },
                sample_every = new { type = "integer", description = "Брать каждый N-й кадр для анализа (по умолчанию 1)." },
                max_frames = new { type = "integer", description = "Максимум кадров для анализа (по умолчанию 3000)." },
                scene_cut_threshold = new { type = "number", description = "Порог резкой смены сцены по шкале 0..255 (по умолчанию 35)." }
            },
            required = new[] { "workspace_path", "burst_dir" }
        })
    },
    new()
    {
        Name = "pdf_capture_burst",
        Description = "Сделать серию изображений страниц PDF. Сохраняет страницы как картинки в подпапку внутри workspace и возвращает JSON с путём и параметрами страниц.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                pdf_path = new { type = "string", description = "Путь к PDF-файлу (абсолютный или относительный к workspace)." },
                from_page = new { type = "integer", description = "Номер первой страницы (1-based, по умолчанию 1)." },
                to_page = new { type = "integer", description = "Номер последней страницы (1-based, по умолчанию конец файла)." },
                dpi = new { type = "integer", description = "Плотность рендера в DPI (по умолчанию 200)." },
                image_format = new { type = "string", description = "Формат кадров: jpg или png (по умолчанию jpg)." },
                jpeg_quality = new { type = "integer", description = "Качество JPEG 1..100 (по умолчанию 92)." },
                output_subdir = new { type = "string", description = @"Подкаталог внутри workspace для сохранения страниц (по умолчанию .cascade-ide\pdf-captures)." },
                burst_name = new { type = "string", description = "Имя серии (опционально, по умолчанию по имени файла)." }
            },
            required = new[] { "workspace_path", "pdf_path" }
        })
    },
    new()
    {
        Name = "ocr_image_batch",
        Description = "Сделать OCR для серии изображений (страницы PDF или burst) через внешний tesseract и вернуть JSON по страницам.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                images_dir = new { type = "string", description = "Путь к папке с изображениями (абсолютный или относительный к workspace)." },
                lang = new { type = "string", description = "Коды языков tesseract, например \"eng+rus\" (по умолчанию eng)." },
                sample_every = new { type = "integer", description = "Брать каждый N-й файл (по умолчанию 1)." },
                max_images = new { type = "integer", description = "Максимум изображений для OCR (по умолчанию 1000)." },
                output_json_path = new { type = "string", description = "Путь для сохранения JSON (относительно workspace). По умолчанию ocr.json в images_dir." }
            },
            required = new[] { "workspace_path", "images_dir" }
        })
    },
    new()
    {
        Name = "analyze_audio_sequence",
        Description = "Проанализировать WAV-файл: громкость, пики, долю тишины, активность речи/звука и таймлайн по окнам.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                audio_path = new { type = "string", description = "Путь к WAV-файлу (абсолютный или относительный к workspace)." },
                frame_ms = new { type = "integer", description = "Размер окна анализа в мс (по умолчанию 50)." },
                silence_threshold_db = new { type = "number", description = "Порог тишины в dBFS (по умолчанию -45)." }
            },
            required = new[] { "workspace_path", "audio_path" }
        })
    },
    new()
    {
        Name = "transcribe_audio_whisper",
        Description = "Локальная транскрипция аудио через Whisper.net (whisper.cpp runtime). Поддерживаются WAV и WebM/MP4/M4A (через FFmpeg в PATH). Возвращает распознанный текст и сегменты с таймкодами.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                audio_path = new { type = "string", description = "Путь к аудиофайлу (абсолютный или относительный к workspace)." },
                model_path = new { type = "string", description = "Путь к ggml/gguf-модели Whisper. Если не задан — берётся из переменной окружения WHISPER_MODEL_PATH." },
                language = new { type = "string", description = "Язык (например ru, en, auto). По умолчанию auto." },
                max_segments = new { type = "integer", description = "Ограничение количества сегментов в ответе (по умолчанию 1000)." }
            },
            required = new[] { "workspace_path", "audio_path" }
        })
    },
    new()
    {
        Name = "analyze_av_sequence",
        Description = "Комплексный анализ A/V-сессии: объединяет динамику движения из кадров и аудио-активность в одном отчёте.",
        InputSchema = Schema(new
        {
            type = "object",
            properties = new
            {
                workspace_path = new { type = "string", description = "Каталог workspace (корень проекта в Cursor)." },
                session_dir = new { type = "string", description = "Путь к директории A/V-сессии (абсолютный или относительный к workspace)." },
                sample_every = new { type = "integer", description = "Видео-анализ: каждый N-й кадр (по умолчанию 1)." },
                max_frames = new { type = "integer", description = "Видео-анализ: максимум кадров (по умолчанию 3000)." },
                scene_cut_threshold = new { type = "number", description = "Видео-анализ: порог scene cut (по умолчанию 35)." },
                audio_frame_ms = new { type = "integer", description = "Аудио-анализ: окно в мс (по умолчанию 50)." },
                silence_threshold_db = new { type = "number", description = "Аудио-анализ: порог тишины dBFS (по умолчанию -45)." }
            },
            required = new[] { "workspace_path", "session_dir" }
        })
    }
    ];
}