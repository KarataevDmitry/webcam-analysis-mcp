# webcam-analysis-mcp

MCP-сервер **анализа и документов**: burst-видео, WAV, A/V-сессии, PDF→изображения, OCR (tesseract), Whisper. Windows, `stdio`, self-contained `win-x64`.

## Зависимости

- Репозиторий **[webcam-mcp-shared](../webcam-mcp-shared)** в соседней папке.
- **Tesseract** (для `ocr_image_batch`): `TESSERACT_PATH` или `C:\Program Files\Tesseract-OCR\tesseract.exe`.
- **Whisper** (для `transcribe_audio_whisper`): модель ggml/gguf и `WHISPER_MODEL_PATH` или параметр `model_path`.
- **FFmpeg** в PATH — для не-WAV в Whisper.
- **Ghostscript / делегаты ImageMagick** — для рендера PDF в `pdf_capture_burst`.

## Сборка и публикация

```powershell
cd webcam-analysis-mcp
dotnet build WebcamAnalysisMcp.sln -c Release
dotnet publish WebcamAnalysisMcp.csproj -c Release -o publish
```

Исполняемый файл: `publish\WebcamAnalysisMcp.exe`.

## Тулы

| Имя | Назначение |
|-----|------------|
| `analyze_burst_sequence` | движение / scene cuts по папке кадров |
| `pdf_capture_burst` | страницы PDF → jpg/png |
| `ocr_image_batch` | OCR папки изображений → JSON |
| `analyze_audio_sequence` | уровень / тишина / таймлайн по WAV |
| `transcribe_audio_whisper` | транскрипт + сегменты |
| `analyze_av_sequence` | объединение анализа `frames` + `audio.wav` сессии |

## Cursor (`mcp.json`)

```json
"webcam-analysis-mcp": {
  "command": "D:\\path\\to\\webcam-analysis-mcp\\publish\\WebcamAnalysisMcp.exe",
  "args": []
}
```
