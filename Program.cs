using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using ImageMagick;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenCvSharp;
using Whisper.net;
using WebcamMcp.Shared;
using static WebcamMcp.Shared.McpDefaults;
using static WebcamMcp.Shared.MotionAnalysis;
using static WebcamMcp.Shared.ToolArgs;
using WebcamAnalysisMcp;

var toolsList = ToolCatalog.Build();



static string HandleAnalyzeBurstSequence(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var burstDirInput = GetRequiredString(args, "burst_dir");
    var sampleEvery = Math.Clamp(GetOptionalInt(args, "sample_every", 1), 1, 120);
    var maxFrames = Math.Clamp(GetOptionalInt(args, "max_frames", 3000), 2, 20000);
    var sceneCutThreshold = Math.Clamp(GetOptionalDouble(args, "scene_cut_threshold", 35), 1, 255);

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    var burstDir = Path.IsPathRooted(burstDirInput)
        ? Path.GetFullPath(burstDirInput)
        : Path.GetFullPath(Path.Combine(workspaceRoot, burstDirInput));

    if (!burstDir.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("burst_dir points outside of workspace_path.");
    }

    if (!Directory.Exists(burstDir))
    {
        throw new ArgumentException($"Burst directory does not exist: {burstDir}");
    }

    var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp"
    };

    var allFrames = Directory
        .EnumerateFiles(burstDir)
        .Where(path => supportedExtensions.Contains(Path.GetExtension(path)))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Take(maxFrames)
        .ToList();

    if (allFrames.Count < 2)
    {
        throw new ArgumentException("Not enough frames in burst_dir for analysis (need at least 2).");
    }

    var sampledFrames = allFrames
        .Where((_, index) => index % sampleEvery == 0)
        .ToList();

    if (sampledFrames.Count < 2)
    {
        sampledFrames = [allFrames[0], allFrames[^1]];
    }

    var timeline = new List<(string From, string To, double MotionScore, string MotionLevel, bool IsSceneCut)>(sampledFrames.Count - 1);
    var sumMotion = 0.0;
    var maxMotion = double.MinValue;
    var minMotion = double.MaxValue;
    var peaks = new List<(int Index, string Frame, double Score, string Level)>();

    using var prev = Cv2.ImRead(sampledFrames[0], ImreadModes.Grayscale);
    if (prev.Empty())
    {
        throw new ArgumentException($"Failed to read frame: {sampledFrames[0]}");
    }

    using var diff = new Mat();
    var previousFile = sampledFrames[0];
    var previous = prev.Clone();

    try
    {
        for (var i = 1; i < sampledFrames.Count; i++)
        {
            var currentFile = sampledFrames[i];
            using var current = Cv2.ImRead(currentFile, ImreadModes.Grayscale);
            if (current.Empty())
            {
                continue;
            }

            if (current.Size() != previous.Size())
            {
                Cv2.Resize(current, current, previous.Size());
            }

            Cv2.Absdiff(previous, current, diff);
            var motionScore = Cv2.Mean(diff).Val0;
            var level = ClassifyMotion(motionScore);

            timeline.Add((
                Path.GetFileName(previousFile),
                Path.GetFileName(currentFile),
                Math.Round(motionScore, 2),
                level,
                motionScore >= sceneCutThreshold));

            sumMotion += motionScore;
            maxMotion = Math.Max(maxMotion, motionScore);
            minMotion = Math.Min(minMotion, motionScore);
            peaks.Add((i, Path.GetFileName(currentFile), motionScore, level));

            current.CopyTo(previous);
            previousFile = currentFile;
        }
    }
    finally
    {
        previous.Dispose();
    }

    if (timeline.Count == 0)
    {
        throw new ArgumentException("Unable to analyze burst frames (no valid frame pairs).");
    }

    var avgMotion = sumMotion / timeline.Count;
    var topPeaks = peaks
        .OrderByDescending(item => item.Score)
        .Take(5)
        .Select(item => new
        {
            frame = item.Frame,
            motion_score = Math.Round(item.Score, 2),
            motion_level = item.Level
        })
        .ToList();

    var sceneCuts = timeline
        .Where(item => item.IsSceneCut)
        .Select(item => new { from = item.From, to = item.To, motion_score = item.MotionScore })
        .ToList();

    var timelineReport = timeline
        .Select(item => new
        {
            from = item.From,
            to = item.To,
            motion_score = item.MotionScore,
            motion_level = item.MotionLevel,
            is_scene_cut = item.IsSceneCut
        })
        .ToList();

    var summaryText = $"Analyzed {sampledFrames.Count} sampled frames from {allFrames.Count} total. " +
                      $"Motion avg={avgMotion:F2}, min={minMotion:F2}, max={maxMotion:F2}. " +
                      $"Scene cuts detected: {sceneCuts.Count}.";

    var result = new
    {
        success = true,
        burst_dir = burstDir,
        total_frames = allFrames.Count,
        sampled_frames = sampledFrames.Count,
        sample_every = sampleEvery,
        avg_motion_score = Math.Round(avgMotion, 2),
        min_motion_score = Math.Round(minMotion, 2),
        max_motion_score = Math.Round(maxMotion, 2),
        scene_cut_threshold = Math.Round(sceneCutThreshold, 2),
        scene_cut_count = sceneCuts.Count,
        top_motion_peaks = topPeaks,
        scene_cuts = sceneCuts,
        timeline = timelineReport,
        summary = summaryText,
        analyzed_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

static string HandlePdfCaptureBurst(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var pdfPathInput = GetRequiredString(args, "pdf_path");
    var fromPageRaw = GetOptionalInt(args, "from_page", 1);
    var toPageRaw = GetOptionalInt(args, "to_page", int.MaxValue);
    var dpi = Math.Clamp(GetOptionalInt(args, "dpi", 200), 72, 600);
    var imageFormat = NormalizeImageFormat(GetOptionalString(args, "image_format") ?? "jpg");
    var jpegQuality = Math.Clamp(GetOptionalInt(args, "jpeg_quality", DefaultJpegQuality), 1, 100);
    var outputSubdir = GetOptionalString(args, "output_subdir") ?? DefaultPdfOutputSubdir;
    var burstName = GetOptionalString(args, "burst_name");

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    var pdfPath = Path.IsPathRooted(pdfPathInput)
        ? Path.GetFullPath(pdfPathInput)
        : Path.GetFullPath(Path.Combine(workspaceRoot, pdfPathInput));

    if (!pdfPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("pdf_path points outside of workspace_path.");
    }

    if (!File.Exists(pdfPath))
    {
        throw new ArgumentException($"PDF file does not exist: {pdfPath}");
    }

    if (!string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("pdf_path must point to a .pdf file.");
    }

    if (Path.IsPathRooted(outputSubdir))
    {
        throw new ArgumentException("output_subdir must be relative to workspace_path.");
    }

    var outputDir = Path.GetFullPath(Path.Combine(workspaceRoot, outputSubdir));
    if (!outputDir.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("output_subdir points outside of workspace_path.");
    }

    Directory.CreateDirectory(outputDir);

    var baseBurstName = string.IsNullOrWhiteSpace(burstName)
        ? Path.GetFileNameWithoutExtension(pdfPath)
        : burstName;
    var safeBurstName = MakeSafeFileName(baseBurstName);
    var burstDir = Path.Combine(outputDir, safeBurstName);
    Directory.CreateDirectory(burstDir);

    var settings = new MagickReadSettings
    {
        Density = new Density(dpi, dpi)
    };

    using var images = new MagickImageCollection();
    images.Read(pdfPath, settings);

    if (images.Count == 0)
    {
        throw new ArgumentException("PDF file contains no pages or cannot be rendered. Ensure Ghostscript/ImageMagick delegates for PDF are installed.");
    }

    var fromPage = Math.Clamp(fromPageRaw, 1, images.Count);
    var toPage = Math.Clamp(toPageRaw, fromPage, images.Count);

    var pages = new List<object>();

    for (var pageIndex = fromPage; pageIndex <= toPage; pageIndex++)
    {
        var image = (MagickImage)images[pageIndex - 1].Clone();
        image.Format = imageFormat switch
        {
            "jpg" => MagickFormat.Jpeg,
            "png" => MagickFormat.Png,
            _ => image.Format
        };

        if (image.Format == MagickFormat.Jpeg)
        {
            image.Quality = (uint)jpegQuality;
        }

        var fileName = $"page-{pageIndex:0000}.{imageFormat}";
        var outputPath = Path.Combine(burstDir, fileName);
        image.Write(outputPath);

        pages.Add(new
        {
            index = pageIndex,
            file = outputPath,
            width = image.Width,
            height = image.Height
        });

        image.Dispose();
    }

    var result = new
    {
        success = true,
        pdf_path = pdfPath,
        burst_dir = burstDir,
        from_page = fromPage,
        to_page = toPage,
        dpi,
        image_format = imageFormat,
        jpeg_quality = jpegQuality,
        page_count = pages.Count,
        pages,
        captured_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

static string HandleOcrImageBatch(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var imagesDirInput = GetRequiredString(args, "images_dir");
    var lang = (GetOptionalString(args, "lang") ?? "eng").Trim();
    var sampleEvery = Math.Clamp(GetOptionalInt(args, "sample_every", 1), 1, 100);
    var maxImages = Math.Clamp(GetOptionalInt(args, "max_images", 1000), 1, 10000);
    var outputJsonPathInput = GetOptionalString(args, "output_json_path");

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    var imagesDir = Path.IsPathRooted(imagesDirInput)
        ? Path.GetFullPath(imagesDirInput)
        : Path.GetFullPath(Path.Combine(workspaceRoot, imagesDirInput));

    if (!imagesDir.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("images_dir points outside of workspace_path.");
    }

    if (!Directory.Exists(imagesDir))
    {
        throw new ArgumentException($"Images directory does not exist: {imagesDir}");
    }

    var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"
    };

    var allImages = Directory
        .EnumerateFiles(imagesDir)
        .Where(path => supportedExtensions.Contains(Path.GetExtension(path)))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (allImages.Count == 0)
    {
        throw new ArgumentException("No image files found in images_dir.");
    }

    var sampledImages = allImages
        .Where((_, index) => index % sampleEvery == 0)
        .Take(maxImages)
        .ToList();

    if (sampledImages.Count == 0)
    {
        sampledImages.Add(allImages[0]);
    }

    var tesseractExe = Environment.GetEnvironmentVariable(EnvTesseractPath);
    if (string.IsNullOrWhiteSpace(tesseractExe))
    {
        var defaultPath = @"C:\Program Files\Tesseract-OCR\tesseract.exe";
        tesseractExe = File.Exists(defaultPath) ? defaultPath : "tesseract";
    }

    var pages = new List<object>();
    var errors = new List<object>();

    for (var i = 0; i < sampledImages.Count; i++)
    {
        var imagePath = sampledImages[i];
        var fileName = Path.GetFileName(imagePath);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = tesseractExe,
                Arguments = $"\"{imagePath}\" stdout -l {lang} --psm 3",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)
                ?? throw new ArgumentException($"Failed to start tesseract process for: {imagePath}");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                errors.Add(new
                {
                    file = imagePath,
                    message = $"tesseract exited with code {process.ExitCode}: {error.Trim()}"
                });
                continue;
            }

            pages.Add(new
            {
                index = i + 1,
                file = imagePath,
                file_name = fileName,
                text = output.Trim()
            });
        }
        catch (Exception ex)
        {
            errors.Add(new
            {
                file = imagePath,
                message = ex.Message
            });
        }
    }

    if (pages.Count == 0 && errors.Count > 0)
    {
        throw new ArgumentException("OCR failed for all images. Check that tesseract is installed and accessible.");
    }

    string? outputJsonPath = null;
    if (!string.IsNullOrWhiteSpace(outputJsonPathInput))
    {
        outputJsonPath = Path.IsPathRooted(outputJsonPathInput)
            ? Path.GetFullPath(outputJsonPathInput)
            : Path.GetFullPath(Path.Combine(workspaceRoot, outputJsonPathInput));
    }
    else
    {
        outputJsonPath = Path.Combine(imagesDir, "ocr.json");
    }

    if (!outputJsonPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("output_json_path points outside of workspace_path.");
    }

    var resultObject = new
    {
        success = true,
        workspace_path = workspaceRoot,
        images_dir = imagesDir,
        lang,
        sample_every = sampleEvery,
        max_images = maxImages,
        images_total = allImages.Count,
        images_processed = pages.Count,
        errors = errors,
        pages,
        generated_at_utc = DateTime.UtcNow.ToString("O")
    };

    Directory.CreateDirectory(Path.GetDirectoryName(outputJsonPath)!);
    File.WriteAllText(outputJsonPath, JsonSerializer.Serialize(resultObject, new JsonSerializerOptions
    {
        WriteIndented = true
    }));

    var result = new
    {
        success = true,
        output_json_path = outputJsonPath,
        images_processed = pages.Count,
        images_total = allImages.Count,
        lang,
        has_errors = errors.Count > 0,
        generated_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}
static string HandleAnalyzeAudioSequence(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var audioPathInput = GetRequiredString(args, "audio_path");
    var frameMs = Math.Clamp(GetOptionalInt(args, "frame_ms", DefaultAudioFrameMs), 10, 500);
    var silenceThresholdDb = Math.Clamp(GetOptionalDouble(args, "silence_threshold_db", DefaultAudioSilenceDb), -120, 0);

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    var audioPath = Path.IsPathRooted(audioPathInput)
        ? Path.GetFullPath(audioPathInput)
        : Path.GetFullPath(Path.Combine(workspaceRoot, audioPathInput));

    if (!audioPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("audio_path points outside of workspace_path.");
    }

    if (!File.Exists(audioPath))
    {
        throw new ArgumentException($"Audio file does not exist: {audioPath}");
    }

    using var reader = new AudioFileReader(audioPath);
    var sampleRate = reader.WaveFormat.SampleRate;
    var channels = reader.WaveFormat.Channels;
    var samplesPerFrame = Math.Max(1, sampleRate * frameMs / 1000);

    var readBuffer = new float[4096 * channels];
    var monoSamples = new List<float>(sampleRate * 10);
    float peakAbs = 0;

    int read;
    while ((read = reader.Read(readBuffer, 0, readBuffer.Length)) > 0)
    {
        for (var index = 0; index < read; index += channels)
        {
            var mono = 0.0f;
            for (var channel = 0; channel < channels && index + channel < read; channel++)
            {
                mono += readBuffer[index + channel];
            }

            mono /= channels;
            var abs = Math.Abs(mono);
            if (abs > peakAbs)
            {
                peakAbs = abs;
            }

            monoSamples.Add(mono);
        }
    }

    if (monoSamples.Count < 2)
    {
        throw new ArgumentException("Audio file is too short for analysis.");
    }

    var timeline = new List<object>();
    var rmsValues = new List<double>();
    var silentFrames = 0;
    var activeFrames = 0;
    var zeroCrossings = 0;

    for (var i = 1; i < monoSamples.Count; i++)
    {
        var prev = monoSamples[i - 1];
        var current = monoSamples[i];
        if ((prev >= 0 && current < 0) || (prev < 0 && current >= 0))
        {
            zeroCrossings++;
        }
    }

    for (var start = 0; start < monoSamples.Count; start += samplesPerFrame)
    {
        var end = Math.Min(monoSamples.Count, start + samplesPerFrame);
        var count = end - start;
        if (count <= 0)
        {
            continue;
        }

        double sumSquares = 0;
        float framePeak = 0;
        for (var i = start; i < end; i++)
        {
            var value = monoSamples[i];
            sumSquares += value * value;
            var abs = Math.Abs(value);
            if (abs > framePeak)
            {
                framePeak = abs;
            }
        }

        var rms = Math.Sqrt(sumSquares / count);
        var db = 20.0 * Math.Log10(Math.Max(1e-9, rms));
        var isSilent = db < silenceThresholdDb;
        if (isSilent)
        {
            silentFrames++;
        }
        else
        {
            activeFrames++;
        }

        rmsValues.Add(rms);
        timeline.Add(new
        {
            start_sec = Math.Round((double)start / sampleRate, 3),
            end_sec = Math.Round((double)end / sampleRate, 3),
            rms = Math.Round(rms, 5),
            dbfs = Math.Round(db, 2),
            peak = Math.Round(framePeak, 5),
            is_silent = isSilent
        });
    }

    var avgRms = rmsValues.Count > 0 ? rmsValues.Average() : 0;
    var maxRms = rmsValues.Count > 0 ? rmsValues.Max() : 0;
    var minRms = rmsValues.Count > 0 ? rmsValues.Min() : 0;
    var durationSeconds = (double)monoSamples.Count / sampleRate;
    var silenceRatio = timeline.Count > 0 ? (double)silentFrames / timeline.Count : 0;
    var activityRatio = timeline.Count > 0 ? (double)activeFrames / timeline.Count : 0;
    var zcr = durationSeconds > 0 ? zeroCrossings / durationSeconds : 0;
    var peakDb = 20.0 * Math.Log10(Math.Max(1e-9, peakAbs));

    var summary = $"Duration {durationSeconds:F2}s, activity {activityRatio:P0}, silence {silenceRatio:P0}, " +
                  $"avg level {20 * Math.Log10(Math.Max(1e-9, avgRms)):F1} dBFS, peak {peakDb:F1} dBFS.";

    var result = new
    {
        success = true,
        audio_path = audioPath,
        sample_rate = sampleRate,
        channels,
        duration_sec = Math.Round(durationSeconds, 3),
        frame_ms = frameMs,
        silence_threshold_db = silenceThresholdDb,
        avg_rms = Math.Round(avgRms, 6),
        min_rms = Math.Round(minRms, 6),
        max_rms = Math.Round(maxRms, 6),
        peak = Math.Round(peakAbs, 6),
        peak_dbfs = Math.Round(peakDb, 2),
        silence_ratio = Math.Round(silenceRatio, 4),
        activity_ratio = Math.Round(activityRatio, 4),
        zero_crossings_per_sec = Math.Round(zcr, 2),
        total_frames = timeline.Count,
        timeline,
        summary,
        analyzed_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

/// <summary>Resample to 16kHz mono and write 16-bit WAV for Whisper.</summary>
static void ConvertToWhisperWav(ISampleProvider reader, string normalizedWavPath)
{
    if (reader.WaveFormat.Channels == 2)
    {
        var stereoToMono = new StereoToMonoSampleProvider(reader)
        {
            LeftVolume = 0.5f,
            RightVolume = 0.5f
        };
        reader = stereoToMono;
    }
    else if (reader.WaveFormat.Channels > 2)
    {
        throw new ArgumentException($"Unsupported channel count: {reader.WaveFormat.Channels}. Use mono/stereo source.");
    }

    var resampled = new WdlResamplingSampleProvider(reader, 16000);
    WaveFileWriter.CreateWaveFile16(normalizedWavPath, resampled);
}

/// <summary>Convert WebM/MP4/etc. to 16kHz mono WAV using FFmpeg. Returns true if successful.</summary>
static bool TryConvertToWavWithFfmpeg(string inputPath, string outputWavPath)
{
    const string ffmpegExe = "ffmpeg";
    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegExe,
            ArgumentList = { "-y", "-i", inputPath, "-acodec", "pcm_s16le", "-ar", "16000", "-ac", "1", outputWavPath },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        using var process = Process.Start(startInfo);
        if (process == null)
            return false;
        process.WaitForExit(TimeSpan.FromMinutes(5));
        return process.ExitCode == 0 && File.Exists(outputWavPath) && new FileInfo(outputWavPath).Length > 44;
    }
    catch
    {
        return false;
    }
}

static string HandleTranscribeAudioWhisper(IReadOnlyDictionary<string, JsonElement> args) =>
    HandleTranscribeAudioWhisperAsync(args).GetAwaiter().GetResult();

static async Task<string> HandleTranscribeAudioWhisperAsync(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var audioPathInput = GetRequiredString(args, "audio_path");
    var modelPathInput = GetOptionalString(args, "model_path");
    var language = (GetOptionalString(args, "language") ?? "auto").Trim().ToLowerInvariant();
    var maxSegments = Math.Clamp(GetOptionalInt(args, "max_segments", 1000), 1, 5000);

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    var audioPath = Path.IsPathRooted(audioPathInput)
        ? Path.GetFullPath(audioPathInput)
        : Path.GetFullPath(Path.Combine(workspaceRoot, audioPathInput));

    if (!audioPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("audio_path points outside of workspace_path.");
    }

    if (!File.Exists(audioPath))
    {
        throw new ArgumentException($"Audio file does not exist: {audioPath}");
    }

    var modelPath = modelPathInput;
    if (string.IsNullOrWhiteSpace(modelPath))
    {
        modelPath = Environment.GetEnvironmentVariable(EnvWhisperModelPath);
    }

    if (string.IsNullOrWhiteSpace(modelPath))
    {
        throw new ArgumentException("model_path is required or set WHISPER_MODEL_PATH env var.");
    }

    modelPath = Path.GetFullPath(modelPath.Trim());
    if (!File.Exists(modelPath))
    {
        throw new ArgumentException($"Whisper model not found: {modelPath}");
    }

    var tempDir = Path.Combine(workspaceRoot, ".cascade-ide", "audio-captures");
    Directory.CreateDirectory(tempDir);
    var normalizedWavPath = Path.Combine(tempDir, $"whisper-input-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.wav");

    // Whisper runtime is most reliable on 16kHz mono PCM WAV. For WebM/MP4/etc. convert via FFmpeg when available.
    var ext = Path.GetExtension(audioPath).TrimStart('.').ToLowerInvariant();
    if (ext == "wav")
    {
        using (var reader = new AudioFileReader(audioPath))
        {
            ConvertToWhisperWav(reader, normalizedWavPath);
        }
    }
    else
    {
        // Non-WAV (e.g. webm, mp4): try FFmpeg to convert to 16kHz mono WAV.
        if (!TryConvertToWavWithFfmpeg(audioPath, normalizedWavPath))
        {
            throw new ArgumentException(
                $"Unsupported audio format: .{ext}. For WebM/MP4/M4A etc. install FFmpeg and add it to PATH, or convert the file to WAV manually (16kHz mono recommended).");
        }
    }

    var segments = new List<object>();
    var transcriptParts = new List<string>();

    try
    {
        using var whisperFactory = WhisperFactory.FromPath(modelPath);
        using var processor = whisperFactory
            .CreateBuilder()
            .WithLanguage(language)
            .Build();

        await using var fileStream = File.OpenRead(normalizedWavPath);
        await foreach (var segment in processor.ProcessAsync(fileStream))
        {
            var text = segment.Text?.Trim() ?? string.Empty;
            if (text.Length > 0)
            {
                transcriptParts.Add(text);
            }

            if (segments.Count < maxSegments)
            {
                segments.Add(new
                {
                    start_sec = Math.Round(segment.Start.TotalSeconds, 3),
                    end_sec = Math.Round(segment.End.TotalSeconds, 3),
                    text
                });
            }
        }
    }
    finally
    {
        try
        {
            if (File.Exists(normalizedWavPath))
            {
                File.Delete(normalizedWavPath);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    var transcript = string.Join(" ", transcriptParts).Trim();
    var result = new
    {
        success = true,
        audio_path = audioPath,
        model_path = modelPath,
        language,
        transcript,
        segments,
        segment_count = segments.Count,
        transcribed_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

static string HandleAnalyzeAvSequence(IReadOnlyDictionary<string, JsonElement> args)
{
    var workspacePath = GetRequiredString(args, "workspace_path");
    var sessionDirInput = GetRequiredString(args, "session_dir");
    var sampleEvery = Math.Clamp(GetOptionalInt(args, "sample_every", 1), 1, 120);
    var maxFrames = Math.Clamp(GetOptionalInt(args, "max_frames", 3000), 2, 20000);
    var sceneCutThreshold = Math.Clamp(GetOptionalDouble(args, "scene_cut_threshold", 35), 1, 255);
    var audioFrameMs = Math.Clamp(GetOptionalInt(args, "audio_frame_ms", DefaultAudioFrameMs), 10, 500);
    var silenceThresholdDb = Math.Clamp(GetOptionalDouble(args, "silence_threshold_db", DefaultAudioSilenceDb), -120, 0);

    var workspaceRoot = Path.GetFullPath(workspacePath.Trim());
    if (File.Exists(workspaceRoot))
    {
        workspaceRoot = Path.GetDirectoryName(workspaceRoot) ?? workspaceRoot;
    }

    if (!Directory.Exists(workspaceRoot))
    {
        throw new ArgumentException($"Workspace directory does not exist: {workspaceRoot}");
    }

    var sessionDir = Path.IsPathRooted(sessionDirInput)
        ? Path.GetFullPath(sessionDirInput)
        : Path.GetFullPath(Path.Combine(workspaceRoot, sessionDirInput));

    if (!sessionDir.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("session_dir points outside of workspace_path.");
    }

    if (!Directory.Exists(sessionDir))
    {
        throw new ArgumentException($"Session directory does not exist: {sessionDir}");
    }

    var framesDir = Path.Combine(sessionDir, "frames");
    var audioPath = Path.Combine(sessionDir, "audio.wav");
    if (!Directory.Exists(framesDir))
    {
        throw new ArgumentException($"Frames directory not found: {framesDir}");
    }

    if (!File.Exists(audioPath))
    {
        throw new ArgumentException($"Audio track not found: {audioPath}");
    }

    var videoArgs = new Dictionary<string, JsonElement>
    {
        ["workspace_path"] = JsonSerializer.SerializeToElement(workspaceRoot),
        ["burst_dir"] = JsonSerializer.SerializeToElement(framesDir),
        ["sample_every"] = JsonSerializer.SerializeToElement(sampleEvery),
        ["max_frames"] = JsonSerializer.SerializeToElement(maxFrames),
        ["scene_cut_threshold"] = JsonSerializer.SerializeToElement(sceneCutThreshold)
    };
    var audioArgs = new Dictionary<string, JsonElement>
    {
        ["workspace_path"] = JsonSerializer.SerializeToElement(workspaceRoot),
        ["audio_path"] = JsonSerializer.SerializeToElement(audioPath),
        ["frame_ms"] = JsonSerializer.SerializeToElement(audioFrameMs),
        ["silence_threshold_db"] = JsonSerializer.SerializeToElement(silenceThresholdDb)
    };

    var videoJson = HandleAnalyzeBurstSequence(videoArgs);
    var audioJson = HandleAnalyzeAudioSequence(audioArgs);
    using var videoDoc = JsonDocument.Parse(videoJson);
    using var audioDoc = JsonDocument.Parse(audioJson);

    var videoRoot = videoDoc.RootElement.Clone();
    var audioRoot = audioDoc.RootElement.Clone();
    var videoSummary = videoRoot.TryGetProperty("summary", out var vs) ? vs.GetString() ?? "" : "";
    var audioSummary = audioRoot.TryGetProperty("summary", out var @as) ? @as.GetString() ?? "" : "";
    var avgMotion = videoRoot.TryGetProperty("avg_motion_score", out var am) ? am.GetDouble() : 0;
    var activityRatio = audioRoot.TryGetProperty("activity_ratio", out var ar) ? ar.GetDouble() : 0;

    var combinedLabel = avgMotion switch
    {
        < 3 when activityRatio < 0.4 => "calm_silent",
        < 3 when activityRatio < 0.7 => "calm_talk",
        < 6 when activityRatio < 0.7 => "active_talk",
        _ => "dynamic"
    };

    var result = new
    {
        success = true,
        session_dir = sessionDir,
        av_profile = combinedLabel,
        summary = $"Video: {videoSummary} Audio: {audioSummary}",
        video_analysis = videoRoot,
        audio_analysis = audioRoot,
        analyzed_at_utc = DateTime.UtcNow.ToString("O")
    };

    return JsonSerializer.Serialize(result);
}

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "WebcamAnalysisMcp", Version = "0.1.0" },
    ProtocolVersion = "2024-11-05",
    Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
    Handlers = new McpServerHandlers
    {
        ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = toolsList }),

        CallToolHandler = (request, _) =>
        {
            var name = request.Params?.Name ?? "";
            var args = request.Params?.Arguments is IReadOnlyDictionary<string, JsonElement> a
                ? a
                : FrozenDictionary<string, JsonElement>.Empty;

            try
            {
                var text = name switch
                {
                    "analyze_burst_sequence" => HandleAnalyzeBurstSequence(args),
                    "pdf_capture_burst" => HandlePdfCaptureBurst(args),
                    "ocr_image_batch" => HandleOcrImageBatch(args),
                    "analyze_audio_sequence" => HandleAnalyzeAudioSequence(args),
                    "transcribe_audio_whisper" => HandleTranscribeAudioWhisper(args),
                    "analyze_av_sequence" => HandleAnalyzeAvSequence(args),
                    _ => throw new ArgumentException($"Unknown tool: {name}.")
                };

                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = text }],
                    IsError = false
                });
            }
            catch (ArgumentException ex)
            {
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Error: {ex.Message}" }],
                    IsError = true
                });
            }
            catch (Exception ex)
            {
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "Error: " + ex.Message }],
                    IsError = true
                });
            }
        }
    }
};

var transport = new StdioServerTransport("WebcamAnalysisMcp");
await using var server = McpServer.Create(transport, options);
await server.RunAsync();
return 0;
