namespace LbReMuxer;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: LbReMuxer <source.webm> <output.webm>\n" +
                                    "Work with any webm videos featuring a single VP8 video track and a single Vorbis audio track");
            return 1;
        }

        string sourcePath = args[0];
        string outputPath = args[1];

        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"Source not found: {sourcePath}");
            return 1;
        }

        try
        {
            byte[] template = ThisAssembly.Resources.good_template.GetBytes();
            byte[] source = File.ReadAllBytes(sourcePath);

            byte[] output = WebmPatcher.Patch(template, source);

            // Create the output directory if it doesn't already exist.
            string? outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            File.WriteAllBytes(outputPath, output);
            Console.WriteLine($"Remuxed file written successfully to: {outputPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to remux \"{sourcePath}\": {ex.Message}");
            Console.Error.WriteLine(
                "The source must be a WebM video with a single VP8 video track and a single Vorbis audio track.");
            return 1;
        }
    }
}
