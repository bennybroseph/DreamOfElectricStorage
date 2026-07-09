using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

public class FileTypeClassifierTests
{
    [Theory]
    [InlineData("photo.PNG", FileTypeCategory.Image)]
    [InlineData("clip.mkv", FileTypeCategory.Video)]
    [InlineData("song.flac", FileTypeCategory.Audio)]
    [InlineData("notes.md", FileTypeCategory.Document)]
    [InlineData("Program.cs", FileTypeCategory.Code)]
    [InlineData("backup.7z", FileTypeCategory.Archive)]
    [InlineData("setup.exe", FileTypeCategory.Executable)]
    [InlineData("flux1-dev.safetensors", FileTypeCategory.Model)]
    [InlineData("re_chunk_000.pak", FileTypeCategory.GameData)]
    [InlineData("kernel32.dll", FileTypeCategory.System)]
    [InlineData("mystery.xyz", FileTypeCategory.Other)]
    [InlineData("no-extension", FileTypeCategory.Other)]
    [InlineData("trailing-dot.", FileTypeCategory.Other)]
    public void Classify_MapsExtensions(string name, FileTypeCategory expected) =>
        Assert.Equal(expected, FileTypeClassifier.Classify(name));
}

public class NameStemTests
{
    [Theory]
    [InlineData("report.docx", "report")]
    [InlineData("report (1).docx", "report")]
    [InlineData("report - Copy.docx", "report")]
    [InlineData("report - Copy (2).docx", "report")]
    [InlineData("report_v2.docx", "report")]
    [InlineData("report-v13.docx", "report")]
    [InlineData("photo_001.jpg", "photo")]
    [InlineData("UPPER (3).TXT", "upper")]
    [InlineData(".gitignore", ".gitignore")]
    public void Normalize_StripsVersionAndCopyDecorations(string name, string expected) =>
        Assert.Equal(expected, NameStem.Normalize(name));

    [Theory]
    [InlineData("report.docx", "report (1).docx", true)]
    [InlineData("report_v2.docx", "report - Copy.docx", true)]
    [InlineData("report.docx", "report.docx", false)] // identical name = duplicate territory, not "similar"
    [InlineData("a.txt", "a (1).txt", false)]          // stem too short to be meaningful
    [InlineData("report.docx", "invoice.docx", false)]
    public void AreSimilar_MatchesStems(string a, string b, bool expected) =>
        Assert.Equal(expected, NameStem.AreSimilar(a, b));
}
