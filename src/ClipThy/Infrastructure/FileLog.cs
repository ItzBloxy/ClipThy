namespace ClipThy.Infrastructure;

/// <summary>Thread-safe append-only log that rolls over to "name.1.ext" at a size limit. Never throws.</summary>
public sealed class FileLog
{
    private readonly object _lock = new();
    private readonly long _maxBytes;

    public FileLog(string path, long maxBytes = 1_000_000)
    {
        FilePath = path;
        _maxBytes = maxBytes;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipThy", "clipthy.log");

    public string FilePath { get; }

    public string RolledPath => Path.Combine(
        Path.GetDirectoryName(FilePath)!,
        Path.GetFileNameWithoutExtension(FilePath) + ".1" + Path.GetExtension(FilePath));

    public void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
        lock (_lock)
        {
            try
            {
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length + line.Length > _maxBytes) File.Move(FilePath, RolledPath, overwrite: true);
                File.AppendAllText(FilePath, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
