using System.IO.Compression;

namespace AspireUI.Server.Services;

public class ExportService
{
    public byte[] Zip(string dir)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                zip.CreateEntryFromFile(file, rel);
            }
        }
        return ms.ToArray();
    }
}
