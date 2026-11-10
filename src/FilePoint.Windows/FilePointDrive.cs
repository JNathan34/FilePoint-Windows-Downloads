using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using DokanNet;

namespace FilePoint.Windows;

internal sealed class FilePointDrive : IDokanOperations
{
    private const FileAttributes VirtualAttributes = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly;
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
    private readonly FilePointApi api;
    private readonly FolderIcons icons;
    private readonly Node root = new("", "", "/", @"\", true, 0, DateTimeOffset.UnixEpoch, false, null);
    private readonly string sddl;
    private readonly SecurityIdentifier user;
    private readonly ConcurrentDictionary<string, CachedDirectory> directories = new(NameComparer);
    private readonly object spaceLock = new();
    private DateTimeOffset spaceExpires;
    private DiskSpace? diskSpace;

    private sealed record Node(string Name, string Source, string Path, string WindowsPath, bool Directory,
        long Length, DateTimeOffset Modified, bool Hidden, IconChoice? Icon, byte[]? Data = null, string IconScope = "")
    {
        internal FileAttributes Attributes => Directory
            ? FileAttributes.Directory | (Icon is null ? 0 : FileAttributes.ReadOnly) |
              (Hidden ? FileAttributes.Hidden : 0)
            : Data is not null ? VirtualAttributes : FileAttributes.ReadOnly | (Hidden ? FileAttributes.Hidden : 0);
    }

    private sealed record CachedDirectory(DateTimeOffset Expires, Lazy<Node[]> Children);

    private sealed class OpenFile(Node node)
    {
        internal Node Node { get; } = node;
        internal byte[]? Block { get; set; }
        internal long Offset { get; set; }
    }

    internal FilePointDrive(FilePointApi api, FolderIcons icons, SecurityIdentifier user)
    {
        this.api = api;
        this.icons = icons;
        this.user = user;
        sddl = $"O:{user}G:{user}D:P(A;;FA;;;SY)(A;;FRFX;;;{user})";
    }

    private static NtStatus Status(Exception exception) => exception switch
    {
        FileNotFoundException => DokanResult.FileNotFound,
        DirectoryNotFoundException => DokanResult.PathNotFound,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => DokanResult.AccessDenied,
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => DokanResult.FileNotFound,
        UnauthorizedAccessException => DokanResult.AccessDenied,
        _ => DokanResult.Error
    };

    public NtStatus CreateFile(string name, DokanNet.FileAccess access, FileShare share,
        FileMode mode, FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        try
        {
            using var identity = info.GetRequestor();
            if (identity?.User is not { } requester || !(requester.Equals(user) || requester.IsWellKnown(WellKnownSidType.LocalSystemSid)))
                return DokanResult.AccessDenied;
            const uint writeOrDelete = 0x500D0116;
            if (mode != FileMode.Open || ((uint)access & writeOrDelete) != 0 || options.HasFlag(FileOptions.DeleteOnClose))
                return ReadOnly;
            var item = Resolve(name);
            if (info.IsDirectory && !item.Directory)
                return DokanResult.NotADirectory;
            info.IsDirectory = item.Directory;
            const uint readData = 0xA0000021; // Read/execute, including generic access.
            info.Context = new OpenFile(((uint)access & readData) == 0 ? item : WithFileLength(item));
            return DokanResult.Success;
        }
        catch (Exception exception) { return Status(exception); }
    }

    public void Cleanup(string name, IDokanFileInfo info) => info.Context = null;
    public void CloseFile(string name, IDokanFileInfo info) => info.Context = null;

    public NtStatus GetFileInformation(string name, out FileInformation information, IDokanFileInfo info)
    {
        information = default;
        try
        {
            information = Info((info.Context as OpenFile)?.Node ?? Resolve(name));
            return DokanResult.Success;
        }
        catch (Exception exception) { return Status(exception); }
    }

    public NtStatus ReadFile(string name, byte[] buffer, out int transferred, long offset, IDokanFileInfo info)
    {
        transferred = 0;
        try
        {
            var open = info.Context as OpenFile ?? new OpenFile(WithFileLength(Resolve(name)));
            var item = open.Node;
            if (item.Directory)
                return (NtStatus)0xC00000BA;
            if (offset < 0)
                return DokanResult.InvalidParameter;
            if (buffer.Length == 0 || offset >= item.Length)
                return DokanResult.Success;

            int count = (int)Math.Min(buffer.Length, item.Length - offset);
            if (item.Data is { } data)
            {
                Array.Copy(data, offset, buffer, 0, count);
            }
            else
            {
                lock (open)
                {
                    int copied = 0;
                    while (copied < count)
                    {
                        long start = offset + copied;
                        if (open.Block is null || start < open.Offset || start >= open.Offset + open.Block.Length)
                        {
                            int blockSize = (int)Math.Min(item.Length - start, Math.Clamp(count - copied, 256 * 1024, 1024 * 1024));
                            open.Block = api.Read(item.Source, item.Path, item.Length, start, blockSize);
                            open.Offset = start;
                        }
                        int inBlock = (int)(start - open.Offset);
                        int chunk = Math.Min(count - copied, open.Block.Length - inBlock);
                        Array.Copy(open.Block, inBlock, buffer, copied, chunk);
                        copied += chunk;
                    }
                }
            }
            transferred = count;
            return DokanResult.Success;
        }
        catch (Exception exception) { return Status(exception); }
    }

    public NtStatus FindFiles(string name, out IList<FileInformation> files, IDokanFileInfo info) =>
        FindFilesWithPattern(name, "*", out files, info);

    public NtStatus FindFilesWithPattern(string name, string pattern, out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = [];
        try
        {
            var folder = (info.Context as OpenFile)?.Node ?? Resolve(name);
            if (!folder.Directory)
                return DokanResult.NotADirectory;
            files = Children(folder)
                .Where(node => FileSystemName.MatchesWin32Expression(pattern, node.Name))
                .Select(Info).ToArray();
            return DokanResult.Success;
        }
        catch (Exception exception) { return Status(exception); }
    }

    public NtStatus GetFileSecurity(string name, out FileSystemSecurity? security,
        AccessControlSections sections, IDokanFileInfo info)
    {
        security = info.IsDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return DokanResult.Success;
    }

    public NtStatus GetVolumeInformation(out string label, out FileSystemFeatures features, out string name,
        out uint maxNameLength, IDokanFileInfo info)
    {
        label = "FilePoint";
        name = "FilePoint";
        maxNameLength = 255;
        features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk |
            FileSystemFeatures.PersistentAcls | FileSystemFeatures.ReadOnlyVolume;
        return DokanResult.Success;
    }

    public NtStatus GetDiskFreeSpace(out long available, out long total, out long free, IDokanFileInfo info)
    {
        available = total = free = 0;
        lock (spaceLock)
        {
            if (spaceExpires <= DateTimeOffset.UtcNow)
            {
                try { diskSpace = api.GetDiskSpace(); }
                catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                {
                    diskSpace = null;
                }
                catch (Exception error) when (IsIconLookupFailure(error) || error is OverflowException or InvalidDataException)
                {
                    // Keep the last known reading through brief server or network outages.
                }
                spaceExpires = DateTimeOffset.UtcNow.AddSeconds(30);
            }
            if (diskSpace is not { } space)
                return DokanResult.NotImplemented;
            available = free = space.Free;
            total = space.Total;
            return DokanResult.Success;
        }
    }

    private const NtStatus ReadOnly = (NtStatus)0xC00000A2;
    public NtStatus WriteFile(string name, byte[] buffer, out int written, long offset, IDokanFileInfo info)
    {
        written = 0;
        return ReadOnly;
    }

    public NtStatus SetFileAttributes(string name, FileAttributes attributes, IDokanFileInfo info) => ReadOnly;
    public NtStatus SetFileTime(string name, DateTime? created, DateTime? accessed, DateTime? written, IDokanFileInfo info) => ReadOnly;
    public NtStatus DeleteFile(string name, IDokanFileInfo info) => ReadOnly;
    public NtStatus DeleteDirectory(string name, IDokanFileInfo info) => ReadOnly;
    public NtStatus MoveFile(string name, string target, bool replace, IDokanFileInfo info) => ReadOnly;
    public NtStatus SetEndOfFile(string name, long length, IDokanFileInfo info) => ReadOnly;
    public NtStatus SetAllocationSize(string name, long length, IDokanFileInfo info) => ReadOnly;
    public NtStatus SetFileSecurity(string name, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info) => ReadOnly;
    public NtStatus LockFile(string name, long offset, long length, IDokanFileInfo info) => DokanResult.NotImplemented;
    public NtStatus UnlockFile(string name, long offset, long length, IDokanFileInfo info) => DokanResult.NotImplemented;
    public NtStatus FlushFileBuffers(string name, IDokanFileInfo info) => DokanResult.Success;
    public NtStatus Mounted(string mountPoint, IDokanFileInfo info) => DokanResult.Success;
    public NtStatus Unmounted(IDokanFileInfo info) => DokanResult.Success;
    public NtStatus FindStreams(string name, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = [];
        return DokanResult.NotImplemented;
    }

    private Node Resolve(string path)
    {
        var folder = root;
        foreach (string part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!folder.Directory)
                throw new DirectoryNotFoundException();
            var found = Children(folder).FirstOrDefault(child => NameComparer.Equals(child.Name, part));
            if (found is null)
                throw new FileNotFoundException();
            folder = found;
        }
        return folder;
    }

    private Node WithFileLength(Node item) => item.Directory || item.Data is not null
        ? item : item with { Length = api.GetFileLength(item.Source, item.Path) };

    private Node[] Children(Node folder)
    {
        var now = DateTimeOffset.UtcNow;
        if (directories.Count > 768)
            directories.Clear();
        var result = directories.AddOrUpdate(folder.WindowsPath,
            _ => CreateCache(folder, now),
            (_, entry) => entry.Expires > now ? entry : CreateCache(folder, now));
        try
        {
            return result.Children.Value;
        }
        catch
        {
            directories.TryRemove(new KeyValuePair<string, CachedDirectory>(folder.WindowsPath, result));
            throw;
        }
    }

    private CachedDirectory CreateCache(Node folder, DateTimeOffset now) =>
        new(now.AddSeconds(15), new Lazy<Node[]>(() => List(folder)));

    private Node[] List(Node folder)
    {
        if (folder.Source.Length == 0)
            return ListSources();

        var listing = api.List(folder.Source, folder.Path);
        var manifest = ReadManifest(folder.Source, folder.IconScope + folder.Path);
        var hidden = new HashSet<string>(manifest?.Hidden ?? [], StringComparer.Ordinal);
        var entries = new Dictionary<string, Node>(NameComparer);
        foreach (var item in listing.Folders ?? [])
            AddRemote(item, true);
        foreach (var item in listing.Files ?? [])
            AddRemote(item, false);

        if (folder.Icon is { } choice)
        {
            try
            {
                var image = icons.Load(choice, api);
                var name = FolderIcons.FileName(choice);
                if (!entries.ContainsKey(name))
                {
                    entries.Add(name, Synthetic(name, image));
                    entries["desktop.ini"] = Synthetic("desktop.ini", FolderIcons.DesktopIni(choice));
                }
            }
            catch (Exception error) when (error is IOException or HttpRequestException or NotSupportedException
                or FileFormatException or OperationCanceledException)
            {
                // An unavailable icon must never prevent browsing the directory.
            }
        }

        return entries.Values.OrderBy(node => node.Name, NameComparer).ToArray();

        void AddRemote(RemoteItem item, bool directory)
        {
            if (!ValidName(item.Name) || item.Name is ".plexpoint-recycle-bin" or ".plexpoint-history")
                return;
            var entry = new Node(item.Name, folder.Source, folder.Path.TrimEnd('/') + "/" + item.Name,
                ChildPath(folder, item.Name), directory, Math.Max(item.Size, 0), item.Modified,
                item.Hidden || hidden.Contains(item.Name), directory ? FindIcon(manifest, item.Name) : null,
                IconScope: folder.IconScope);
            entries.TryAdd(item.Name, entry);
        }

        Node Synthetic(string name, byte[] bytes) => new(name, folder.Source, "", ChildPath(folder, name),
            false, bytes.Length, DateTimeOffset.UnixEpoch, true, null, bytes);
    }

    private Node[] ListSources()
    {
        var scopes = api.GetAccount().Scopes ?? [];
        var home = scopes.Any(scope => scope.Name == "Home" && string.IsNullOrEmpty(scope.Scope?.Trim('/')))
            ? ReadManifest("Home", "") : null;
        return scopes.Where(scope => scope.Name != "Home" && ValidName(scope.Name))
            .DistinctBy(scope => scope.Name, NameComparer)
            .AsParallel().WithDegreeOfParallelism(4)
            .Select(scope =>
            {
                string iconScope = scope.Scope?.Trim('/') ?? "";
                var choice = iconScope.Length == 0 && home?.Icons is not null
                    ? FindIcon(home, scope.Name) : ReadSourceRootIcon(scope.Name, iconScope);
                return new Node(scope.Name, scope.Name, "/", @"\" + scope.Name,
                    true, 0, DateTimeOffset.UnixEpoch, false, choice, IconScope: iconScope);
            })
            .OrderBy(node => node.Name, NameComparer).ToArray();
    }

    private IconChoice? ReadSourceRootIcon(string source, string path)
    {
        try { return api.GetSourceRootIcon(source, path); }
        catch (Exception error) when (IsIconLookupFailure(error)) { return null; }
    }

    private IconManifest? ReadManifest(string source, string path)
    {
        try
        {
            return api.GetIcons(source, path);
        }
        catch (Exception error) when (IsIconLookupFailure(error))
        {
            return null;
        }
    }

    private static bool IsIconLookupFailure(Exception error) =>
        error is HttpRequestException or System.Text.Json.JsonException or IOException or OperationCanceledException;

    private static IconChoice? FindIcon(IconManifest? manifest, string name) =>
        manifest?.Icons is { } choices && choices.TryGetValue(name, out var choice) ? choice : null;

    private static string ChildPath(Node folder, string name) => folder.WindowsPath.TrimEnd('\\') + "\\" + name;

    private static bool ValidName(string name)
    {
        if (name.Length is 0 or > 255 || name[^1] is '.' or ' ' || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        string stem = name.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            && !(stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT"))
                && "123456789¹²³".Contains(stem[3]));
    }

    private static FileInformation Info(Node item)
    {
        DateTime time = item.Modified.Year < 1601 ? DateTime.UnixEpoch : item.Modified.UtcDateTime;
        return new FileInformation
        {
            FileName = item.Name,
            Attributes = item.Attributes,
            Length = item.Directory ? 0 : item.Length,
            CreationTime = time,
            LastWriteTime = time,
            LastAccessTime = time
        };
    }
}
