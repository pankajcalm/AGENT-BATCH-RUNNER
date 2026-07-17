using System.Text;

namespace AgentBatchRunner.Infrastructure;

public static class Utf8File
{
    public static UTF8Encoding Encoding { get; } = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        return File.WriteAllTextAsync(path, contents, Encoding, cancellationToken);
    }

    public static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        return File.ReadAllTextAsync(path, Encoding, cancellationToken);
    }

    public static void WriteAllText(string path, string contents)
    {
        File.WriteAllText(path, contents, Encoding);
    }

    public static string ReadAllText(string path)
    {
        return File.ReadAllText(path, Encoding);
    }
}
