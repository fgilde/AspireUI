using System.IO.Compression;
using AspireUI.Server.Services;

public class ExportTests
{
    [Fact]
    public void Zip_ContainsProjectFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aspireui-zip-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "// x");
        File.WriteAllText(Path.Combine(dir, "Demo.csproj"), "<Project/>");

        var bytes = new ExportService().Zip(dir);

        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Contains(zip.Entries, e => e.FullName == "Program.cs");
        Assert.Contains(zip.Entries, e => e.FullName == "Demo.csproj");
        Directory.Delete(dir, true);
    }
}
