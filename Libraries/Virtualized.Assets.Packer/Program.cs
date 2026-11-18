using System.IO.Compression;
using Virtualized.Assets;

// vpak [-c <level>] [-e <pattern>]... [-k <key file>] [-q] <source directory> <output.vpak>
// vpak --generate-key <key file>
// Run by Virtualized's build to pack wwwroot into the encrypted archive embedded in the executable.
// Key files hold the key as hex text.

var options = new AssetPackOptions { Log = Console.WriteLine };
var positional = new List<string>();

try
{
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--generate-key" when i + 1 < args.Length && args.Length == 2:
                File.WriteAllText(args[++i], Convert.ToHexString(AssetPacker.GenerateKey()));
                return 0;
            case "-c" or "--compression" when i + 1 < args.Length:
                if (!Enum.TryParse<CompressionLevel>(args[++i], ignoreCase: true, out var level))
                    return _Usage($"Unknown compression level '{args[i]}'.");
                options.Compression = level;
                break;
            case "-e" or "--exclude" when i + 1 < args.Length:
                options.Exclude.Add(args[++i]);
                break;
            case "-k" or "--key" when i + 1 < args.Length:
                options.Key = _ReadKey(args[++i]);
                if (options.Key is null)
                    return _Usage($"'{args[i]}' doesn't hold a {AssetPacker.KeySize}-byte key in hex.");
                break;
            case "-q" or "--quiet":
                options.Log = null;
                break;
            case "-h" or "--help":
                return _Usage(null);
            default:
                if (args[i].StartsWith('-'))
                    return _Usage($"Unknown option '{args[i]}'.");
                positional.Add(args[i]);
                break;
        }
    }

    if (positional.Count != 2)
        return _Usage("Expected a source directory and an output file.");

    var result = AssetPacker.Pack(positional[0], positional[1], options);
    var encrypted = options.Key is null ? "" : ", encrypted";
    Console.WriteLine($"Packed {result.FileCount} files into {positional[1]} ({result.OutputSize} bytes{encrypted}).");
    return 0;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"vpak: {e.Message}");
    return 1;
}

static byte[]? _ReadKey(string path)
{
    try
    {
        var key = Convert.FromHexString(File.ReadAllText(path).Trim());
        return key.Length == AssetPacker.KeySize ? key : null;
    }
    catch (FormatException)
    {
        return null;
    }
}

static int _Usage(string? error)
{
    if (error is not null)
        Console.Error.WriteLine($"vpak: {error}");
    Console.WriteLine("Usage: vpak [-c Optimal|Fastest|SmallestSize|NoCompression] [-e <pattern>]... [-k <key file>] [-q] <source directory> <output.vpak>");
    Console.WriteLine("       vpak --generate-key <key file>");
    return error is null ? 0 : 2;
}
