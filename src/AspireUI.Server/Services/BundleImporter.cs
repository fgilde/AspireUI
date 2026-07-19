using System.Text;
using System.Xml.Linq;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

public record BundleFile(string Path, string Content);

// Turns a set of source files (.cs + .csproj, from a folder or zip) into an editable stack:
// picks the AppHost Program.cs, hands it to ImportService for the usual node/edge/raw parsing,
// then folds the csproj's extra <PackageReference>/<ProjectReference> and any leftover .cs files
// into ExtraPackages/ExtraFiles so the round-tripped project still compiles.
public class BundleImporter
{
    private const long MaxBundleBytes = 5 * 1024 * 1024;

    private readonly ImportService _import = new();

    public (StackModel? Stack, string? Error, int StatusCode) Import(
        string id, string name, List<BundleFile> files, string? programPath)
    {
        var totalBytes = files.Sum(f => Encoding.UTF8.GetByteCount(f.Content));
        if (totalBytes > MaxBundleBytes) return (null, "bundle exceeds 5 MB limit", StatusCodes.Status413PayloadTooLarge);

        var programFile = programPath is not null
            ? files.FirstOrDefault(f => f.Path == programPath)
            : files.FirstOrDefault(f => f.Content.Contains("DistributedApplication.CreateBuilder"));
        if (programFile is null) return (null, "no AppHost Program.cs found", StatusCodes.Status422UnprocessableEntity);

        var stack = _import.Import(id, name, programFile.Content, "");

        var extraPackages = new List<PackageRef>();
        var extraRaws = new List<string>();
        var csproj = files.FirstOrDefault(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (csproj is not null) ParseCsproj(csproj.Content, extraPackages, extraRaws);

        var extraFiles = files
            .Where(f => f != programFile && f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(f => new ExtraFile(f.Path, f.Content))
            .ToList();

        stack = stack with
        {
            RawStatements = [.. stack.RawStatements, .. extraRaws],
            ExtraFiles = extraFiles,
            ExtraPackages = extraPackages,
        };
        return (stack, null, StatusCodes.Status200OK);
    }

    private static void ParseCsproj(string xml, List<PackageRef> extraPackages, List<string> extraRaws)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return; } // malformed csproj: just skip extras, the parsed Program.cs still stands

        var knownPackages = CatalogService.ResourcePackages();
        var skipIds = new HashSet<string>(knownPackages.Values.Select(p => p.Id), StringComparer.Ordinal)
        {
            "Aspire.Hosting.AppHost",
        };

        foreach (var pr in doc.Descendants("PackageReference"))
        {
            var pkgId = (string?)pr.Attribute("Include");
            if (pkgId is null || skipIds.Contains(pkgId)) continue;
            extraPackages.Add(new PackageRef(pkgId, (string?)pr.Attribute("Version") ?? ""));
        }

        foreach (var pref in doc.Descendants("ProjectReference"))
        {
            var include = (string?)pref.Attribute("Include");
            if (include is null) continue;
            var refName = Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));
            var known = knownPackages.Values.FirstOrDefault(p => p.Id == refName);
            if (known.Id is not null)
                extraPackages.Add(new PackageRef(known.Id, known.Version ?? CodeGenService.AspireVersion));
            else
                extraRaws.Add($"// TODO import: unresolved project reference {refName}");
        }
    }
}
