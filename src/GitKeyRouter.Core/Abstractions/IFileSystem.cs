namespace GitKeyRouter.Core.Abstractions;

public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    FileAttributes GetAttributes(string path);

    DateTimeOffset GetLastWriteTimeUtc(string path);

    void CreateDirectory(string path);

    void DeleteDirectory(string path, bool recursive);

    void MoveDirectory(string sourcePath, string destinationPath);

    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default);

    Task WriteAllTextAtomicAsync(string path, string content, CancellationToken cancellationToken = default);

    Task WriteAllBytesAtomicAsync(string path, byte[] content, CancellationToken cancellationToken = default);

    void CopyFile(string sourcePath, string destinationPath, bool overwrite);

    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    void DeleteFile(string path);

    IEnumerable<string> EnumerateDirectories(string path);

    IEnumerable<string> EnumerateFiles(string path, string searchPattern);
}
