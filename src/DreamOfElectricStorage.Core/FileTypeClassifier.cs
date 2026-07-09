namespace DreamOfElectricStorage.Core;

public enum FileTypeCategory
{
    Other,
    Image,
    Video,
    Audio,
    Document,
    Code,
    Archive,
    Executable,
    Model,      // AI/ML weights
    GameData,   // packed game assets
    System,
}

/// <summary>Extension → coarse category. Pure; the category is computed, never stored.</summary>
public static class FileTypeClassifier
{
    public static FileTypeCategory Classify(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
            return FileTypeCategory.Other;

        return name[(dot + 1)..].ToLowerInvariant() switch
        {
            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "svg" or "ico" or "tiff" or "tga" or "dds" or "psd" or "heic" or "avif" => FileTypeCategory.Image,
            "mp4" or "mkv" or "avi" or "mov" or "webm" or "wmv" or "flv" or "m4v" or "mpg" or "mpeg" or "ts" => FileTypeCategory.Video,
            "mp3" or "wav" or "flac" or "ogg" or "m4a" or "aac" or "wma" or "opus" or "mid" or "midi" => FileTypeCategory.Audio,
            "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "txt" or "md" or "rtf" or "odt" or "epub" or "csv" => FileTypeCategory.Document,
            "cs" or "js" or "ts" or "tsx" or "py" or "cpp" or "c" or "h" or "hpp" or "java" or "rs" or "go" or "lua" or "sh" or "ps1"
                or "html" or "css" or "xaml" or "xml" or "json" or "yaml" or "yml" or "toml" or "sql" or "csproj" or "sln" => FileTypeCategory.Code,
            "zip" or "rar" or "7z" or "tar" or "gz" or "bz2" or "xz" or "cab" or "iso" or "zst" => FileTypeCategory.Archive,
            "exe" or "msi" or "bat" or "cmd" or "com" or "appx" or "msix" => FileTypeCategory.Executable,
            "safetensors" or "gguf" or "ckpt" or "onnx" or "pt" or "pth" or "bin" or "vae" => FileTypeCategory.Model,
            "pak" or "ucas" or "utoc" or "bdt" or "bhd" or "vpk" or "wad" or "bsa" or "ba2" or "cpk" or "nsp" or "xci" or "vhdx" or "wim" => FileTypeCategory.GameData,
            "dll" or "sys" or "drv" or "ini" or "cat" or "mui" or "manifest" or "log" or "tmp" or "etl" or "db" or "dat" => FileTypeCategory.System,
            _ => FileTypeCategory.Other,
        };
    }
}
