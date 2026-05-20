namespace SVNManager;

internal interface IFileSystem
{
    bool Exists(string path);
    byte[] ReadAllBytes(string path);
    FileInfo GetFileInfo(string path);
}

internal sealed class DefaultFileSystem : IFileSystem
{
    public bool Exists(string path) => File.Exists(path);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public FileInfo GetFileInfo(string path) => new(path);
}
