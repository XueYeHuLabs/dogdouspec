using System.Text;

namespace DogdouSpec.Cli;

internal static class CliEntryPoint
{
    private static int Main(string[] args)
    {
        // XML declarations and exported text promise UTF-8, including redirected Windows output.
        // Keep process setup separate from Program.Main so in-process callers retain their writers.
        Console.OutputEncoding = new UTF8Encoding(false);
        return Program.Main(args);
    }
}
