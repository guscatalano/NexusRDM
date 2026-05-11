namespace NexusRDM.Core.Models;

/// <summary>
/// One directory entry returned by an SFTP listing. Symmetric for local
/// browsing too — the SFTP view's local pane uses the same record so
/// the two columns can share rendering / sorting code.
/// </summary>
public sealed record SftpEntry(
    string         Name,
    string         FullPath,
    bool           IsDirectory,
    bool           IsSymlink,
    long           Size,
    DateTimeOffset LastModified,
    short          Permissions);
