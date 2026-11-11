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
using System.Text;
using DokanNet;

namespace FilePoint.Windows;

internal sealed class FilePointDrive : IDokanOperations
{
    private const FileAttributes VirtualAttributes = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly;
    private const string DevicesFolder = "Connected devices";
    private const string RecycleBinShortcut = "Recycle Bin.url";
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
    private readonly FilePointApi api;
    private readonly FolderIcons icons;
    private readonly Node root = new("", "", "/", @"\", true, 0, DateTimeOffset.UnixEpoch, false, null);
    private readonly string sddl;
    private readonly SecurityIdentifier user;
    private readonly HashSet<string> writableSources;
    private readonly Action<string>? uploadFailed;
    private readonly bool canRecycle;
    private readonly bool hasRecycleBin;
    private readonly Action<string>? recycleFailed;
    private readonly ConcurrentDictionary<string, byte> writers = new(NameComparer);
    internal bool CanWrite => writableSources.Count > 0;
    private readonly ConcurrentDictionary<string, CachedDirectory> directories = new(NameComparer);
    private readonly object spaceLock = new();
    private DateTimeOffset spaceExpires;
    private DiskSpace? diskSpace;

    private sealed record Node(string Name, string Source, string Path, string WindowsPath, bool Directory,
        long Length, DateTimeOffset Modified, bool Hidden, IconChoice? Icon, byte[]? Data = null, string IconScope = "",
        bool IsDeviceGroup = false, bool VisibleShortcut = false)
    {
        internal FileAttributes Attributes => Directory
            ? FileAttributes.Directory | (Icon is null ? 0 : FileAttributes.ReadOnly) |
              (Hidden ? FileAttributes.Hidden : 0)
            : Data is not null && !VisibleShortcut ? VirtualAttributes :
              FileAttributes.ReadOnly | (Hidden ? FileAttributes.Hidden : 0);
    }

    private sealed record CachedDirectory(DateTimeOffset Expires, Lazy<Node[]> Children);

    private sealed class OpenFile(Node node, WritableFile? writable = null)
    {
        internal Node Node { get; set; } = node;
        internal WritableFile? Writable { get; } = writable;
        internal bool DeletePending { get; set; }
        internal bool FailedWrite { get; set; }
        internal byte[]? Block { get; set; }
        internal long Offset { get; set; }
    }

    internal FilePointDrive(FilePointApi api, FolderIcons icons, SecurityIdentifier user,
        IEnumerable<string>? writableSources = null, Action<string>? uploadFailed = null,
        bool canRecycle = false, Action<string>? recycleFailed = null, bool hasRecycleBin = false)
    {
        this.api = api;
        this.icons = icons;
        this.user = user;
        this.writableSources = new HashSet<string>(writableSources ?? [], StringComparer.Ordinal);
        this.uploadFailed = uploadFailed;
        this.canRecycle = canRecycle;
        this.hasRecycleBin = hasRecycleBin;
        this.recycleFailed = recycleFailed;
        sddl = $"O:{user}G:{user}D:P(A;;FA;;;SY)(A;;{(CanWrite ? "FA" : "FRFX")};;;{user})";
    }

    private static NtStatus Status(Exception exception) => exception switch
    {
        FileNotFoundException => DokanResult.FileNotFound,
        DirectoryNotFoundException => DokanResult.PathNotFound,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => DokanResult.AccessDenied,
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => DokanResult.FileNotFound,
        HttpRequestException { StatusCode: HttpStatusCode.Conflict } => DokanResult.SharingViolation,
        HttpRequestException { StatusCode: (HttpStatusCode)507 } => DokanResult.DiskFull,
        IOException error when (error.HResult & 0xffff) is 39 or 112 => DokanResult.DiskFull,
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
            bool write = mode != FileMode.Open || ((uint)access & writeOrDelete) != 0 || options.HasFlag(FileOptions.DeleteOnClose);
            if (write && !CanWrite)
                return ReadOnly;
            Node item;
            try { item = Resolve(name); }
            catch (FileNotFoundException) when (write && mode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate or FileMode.Append)
            {
                return OpenNew(name, attributes, options, info);
            }
            if (info.IsDirectory && !item.Directory)
                return DokanResult.NotADirectory;
            info.IsDirectory = item.Directory;
            if (write)
            {
                if (mode == FileMode.CreateNew)
                    return DokanResult.FileExists;
                if (!IsWritable(item))
                    return ReadOnly;
                if (options.HasFlag(FileOptions.DeleteOnClose))
                {
                    var status = CheckRecycle(item, null);
                    if (status != DokanResult.Success)
                        return status;
                }
                const uint writeData = 0x50000006;
                if (!item.Directory && (mode is FileMode.Create or FileMode.Truncate or FileMode.Append ||
                    ((uint)access & writeData) != 0))
                    return OpenWritable(item, true, mode is FileMode.Create or FileMode.Truncate, info);
            }
            const uint readData = 0xA0000021; // Read/execute, including generic access.
            info.Context = new OpenFile(((uint)access & readData) == 0 ? item : WithFileLength(item));
            return DokanResult.Success;
        }
        catch (Exception exception) { return Status(exception); }
    }

    private bool IsWritable(Node node) => node.Data is null && writableSources.Contains(node.Source);

    private NtStatus OpenNew(string name, FileAttributes attributes, FileOptions options, IDokanFileInfo info)
    {
        int separator = name.LastIndexOf('\\');
        if (separator <= 0)
            return ReadOnly;
        string leaf = name[(separator + 1)..];
        if (!ValidName(leaf) || leaf.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith(".filepoint-", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals(".plexpoint-recycle-bin", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals(".plexpoint-history", StringComparison.OrdinalIgnoreCase))
            return DokanResult.AccessDenied;
        var parent = Resolve(name[..separator]);
        if (!parent.Directory)
            return DokanResult.NotADirectory;
        if (!IsWritable(parent))
            return ReadOnly;
        bool directory = info.IsDirectory || attributes.HasFlag(FileAttributes.Directory);
        var item = new Node(leaf, parent.Source, parent.Path.TrimEnd('/') + "/" + leaf, ChildPath(parent, leaf),
            directory, 0, DateTimeOffset.UtcNow, false, null, IconScope: parent.IconScope);
        if (!directory)
            return OpenWritable(item, false, true, info);
        if (options.HasFlag(FileOptions.DeleteOnClose))
            return ReadOnly;
        api.CreateDirectory(item.Source, item.Path);
        directories.TryRemove(parent.WindowsPath, out _);
        info.IsDirectory = true;
        info.Context = new OpenFile(item);
        return DokanResult.Success;
    }

    private NtStatus OpenWritable(Node item, bool exists, bool truncate, IDokanFileInfo info)
    {
        if (!writers.TryAdd(item.WindowsPath, 0))
            return DokanResult.SharingViolation;
        try
        {
            var local = new WritableFile(api, item.Source, item.Path, item.Name, exists, truncate,
                () => info.TryResetTimeout(60_000));
            info.Context = new OpenFile(item, local);
            return DokanResult.Success;
        }
        catch
        {
            writers.TryRemove(item.WindowsPath, out _);
            throw;
        }
    }

    public void Cleanup(string name, IDokanFileInfo info)
    {
        if (info.Context is not OpenFile open)
            return;
        lock (open)
        {
            open.DeletePending = info.DeletePending;
            if (open.DeletePending && open.Writable?.IsNew != true && CanRecycle(open.Node))
            {
                var node = open.Node;
                bool reserved = open.Writable is not null || writers.TryAdd(node.WindowsPath, 0);
                try
                {
                    if (!reserved)
                        throw new IOException("The item is open for writing.");
                    api.Recycle(node.Source, node.Path);
                    open.Writable?.Discard();
                    directories.TryRemove(node.WindowsPath[..node.WindowsPath.LastIndexOf('\\')], out _);
                    if (node.Directory)
                        directories.Clear();
                }
                catch (Exception)
                {
                    recycleFailed?.Invoke(node.Name);
                    if (open.Writable is not null)
                        open.FailedWrite = true; // Preserve any unsaved changes locally.
                }
                finally
                {
                    if (reserved && open.Writable is null)
                        writers.TryRemove(node.WindowsPath, out _);
                }
            }
            else if (open.DeletePending && open.Writable?.IsNew == true)
            {
                open.Writable.Discard();
            }
            if (open.Writable is null)
                info.Context = null;
        }
    }

    public void CloseFile(string name, IDokanFileInfo info)
    {
        if (info.Context is not OpenFile { Writable: { } local } open)
            return;
        info.Context = null;
        try
        {
            lock (open)
            {
                using (local)
                {
                    if (!open.DeletePending && !info.DeletePending && !open.FailedWrite)
                        Save(open, local, info);
                }
            }
        }
        catch (Exception) { uploadFailed?.Invoke(local.LocalPath); }
        finally { writers.TryRemove(open.Node.WindowsPath, out _); }
    }

    public NtStatus GetFileInformation(string name, out FileInformation information, IDokanFileInfo info)
    {
        information = default;
        try
        {
            var open = info.Context as OpenFile;
            var item = open?.Node ?? Resolve(name);
            if (open?.Writable is { } local)
            {
                lock (open)
                    item = item with { Length = local.Data.Length };
            }
            information = Info(item);
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
            if (open.Writable is { } local)
            {
                lock (open)
                {
                    local.Data.Position = offset;
                    transferred = local.Data.Read(buffer);
                }
                return DokanResult.Success;
            }
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
                // Explorer opens the icon files by name. Wildcard enumeration feeds folder copy and delete.
                .Where(node => node.Data is null || node.VisibleShortcut || NameComparer.Equals(pattern, node.Name))
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
            FileSystemFeatures.PersistentAcls | (CanWrite ? 0 : FileSystemFeatures.ReadOnlyVolume);
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
        if (info.Context is not OpenFile { Writable: { } local } open)
            return ReadOnly;
        try
        {
            lock (open)
            {
                local.Data.Position = info.WriteToEndOfFile ? local.Data.Length : offset;
                local.MarkChanged();
                local.Data.Write(buffer);
                written = buffer.Length;
            }
            return DokanResult.Success;
        }
        catch (Exception exception)
        {
            open.FailedWrite = true;
            uploadFailed?.Invoke(local.LocalPath);
            return Status(exception);
        }
    }

    public NtStatus SetFileAttributes(string name, FileAttributes attributes, IDokanFileInfo info) => WritableMetadata(info);
    public NtStatus SetFileTime(string name, DateTime? created, DateTime? accessed, DateTime? written, IDokanFileInfo info) => WritableMetadata(info);
    private NtStatus WritableMetadata(IDokanFileInfo info) => info.Context is OpenFile open && IsWritable(open.Node)
        ? DokanResult.Success : ReadOnly;
    private bool CanRecycle(Node node) => canRecycle && IsWritable(node) && node.Path != "/";

    private NtStatus CheckRecycle(Node node, WritableFile? writable)
    {
        if (writable?.IsNew == true)
            return DokanResult.Success;
        if (!CanRecycle(node))
            return ReadOnly;
        if (writable is null && writers.ContainsKey(node.WindowsPath))
            return DokanResult.SharingViolation;
        if (node.Directory)
        {
            var listing = api.List(node.Source, node.Path);
            if (listing.Files is { Length: > 0 } || listing.Folders is { Length: > 0 })
                return NtStatus.DirectoryNotEmpty;
        }
        return DokanResult.Success;
    }

    public NtStatus DeleteFile(string name, IDokanFileInfo info)
    {
        try
        {
            var open = info.Context as OpenFile;
            return CheckRecycle(open?.Node ?? Resolve(name), open?.Writable);
        }
        catch (Exception exception) { return Status(exception); }
    }

    public NtStatus DeleteDirectory(string name, IDokanFileInfo info) => DeleteFile(name, info);
    public NtStatus MoveFile(string name, string target, bool replace, IDokanFileInfo info)
    {
        if (info.Context is not OpenFile open || !IsWritable(open.Node))
            return ReadOnly;
        try
        {
            lock (open)
            {
                var node = open.Node;
                int targetSeparator = target.LastIndexOf('\\');
                if (targetSeparator <= 0 || node.Path == "/" || open.FailedWrite)
                    return DokanResult.AccessDenied;
                var parent = Resolve(target[..targetSeparator]);
                string leaf = target[(targetSeparator + 1)..];
                if (!parent.Directory || !ValidName(leaf) || !IsWritable(parent))
                    return DokanResult.AccessDenied;
                // A Shell cross-source move copies first and sends the original to FilePoint's Recycle Bin.
                if (node.Source != parent.Source)
                    return CanRecycle(node) ? NtStatus.NotSameDevice : DokanResult.AccessDenied;
                string targetWindowsPath = ChildPath(parent, leaf);
                bool sameName = NameComparer.Equals(node.WindowsPath, targetWindowsPath);
                var destination = sameName ? null : Children(parent).FirstOrDefault(child => NameComparer.Equals(child.Name, leaf));
                if (destination is not null && (!replace || destination.Directory || !IsWritable(destination)))
                    return DokanResult.FileExists;
                string targetPath = destination?.Path ?? parent.Path.TrimEnd('/') + "/" + leaf;
                if (targetPath == node.Path)
                    return DokanResult.Success;
                bool reserveSource = open.Writable is null;
                if (reserveSource && !writers.TryAdd(node.WindowsPath, 0))
                    return DokanResult.SharingViolation;
                if (!sameName && !writers.TryAdd(targetWindowsPath, 0))
                {
                    if (reserveSource)
                        writers.TryRemove(node.WindowsPath, out _);
                    return DokanResult.SharingViolation;
                }
                try
                {
                    if (open.Writable is { } local)
                        Save(open, local, info);
                    var moved = api.Rename(node.Source, node.Path, targetPath, replace: destination is not null);
                    open.Writable?.Renamed(targetPath, moved.Revision);
                    open.Node = node with { Name = destination?.Name ?? leaf, Path = targetPath, WindowsPath = targetWindowsPath };
                    directories.TryRemove(node.WindowsPath[..node.WindowsPath.LastIndexOf('\\')], out _);
                    directories.TryRemove(parent.WindowsPath, out _);
                    if (node.Directory)
                        directories.Clear();
                    if (!sameName && !reserveSource)
                        writers.TryRemove(node.WindowsPath, out _);
                    return DokanResult.Success;
                }
                finally
                {
                    if (reserveSource)
                        writers.TryRemove(node.WindowsPath, out _);
                    if (!sameName && (reserveSource || open.Node == node))
                        writers.TryRemove(targetWindowsPath, out _);
                }
            }
        }
        catch (Exception exception) { return Status(exception); }
    }
    public NtStatus SetEndOfFile(string name, long length, IDokanFileInfo info) => Resize(length, info, false);
    public NtStatus SetAllocationSize(string name, long length, IDokanFileInfo info) => Resize(length, info, true);
    private NtStatus Resize(long length, IDokanFileInfo info, bool allocation)
    {
        if (info.Context is not OpenFile { Writable: { } local } open)
            return ReadOnly;
        try
        {
            lock (open)
            {
                if (!allocation || length < local.Data.Length)
                {
                    local.MarkChanged();
                    local.Data.SetLength(length);
                }
            }
            return DokanResult.Success;
        }
        catch (Exception exception)
        {
            open.FailedWrite = true;
            uploadFailed?.Invoke(local.LocalPath);
            return Status(exception);
        }
    }
    public NtStatus SetFileSecurity(string name, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info) => ReadOnly;
    public NtStatus LockFile(string name, long offset, long length, IDokanFileInfo info) => DokanResult.NotImplemented;
    public NtStatus UnlockFile(string name, long offset, long length, IDokanFileInfo info) => DokanResult.NotImplemented;
    public NtStatus FlushFileBuffers(string name, IDokanFileInfo info)
    {
        if (info.Context is not OpenFile { Writable: { } local } open)
            return DokanResult.Success;
        try
        {
            lock (open)
            {
                if (open.FailedWrite)
                    return DokanResult.Error;
                Save(open, local, info);
            }
            return DokanResult.Success;
        }
        catch (Exception exception)
        {
            uploadFailed?.Invoke(local.LocalPath);
            return Status(exception);
        }
    }

    private void Save(OpenFile open, WritableFile local, IDokanFileInfo info)
    {
        local.Save(() => info.TryResetTimeout(60_000));
        string path = open.Node.WindowsPath;
        directories.TryRemove(path[..path.LastIndexOf('\\')], out _);
    }
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
        new(now.AddSeconds(folder.IsDeviceGroup ? 3 : 15), new Lazy<Node[]>(() => List(folder)));

    private Node[] List(Node folder)
    {
        if (folder.Source.Length == 0)
            return ListSources();
        if (folder.IsDeviceGroup)
            return ListDevices(folder);

        var listing = api.List(folder.Source, folder.Path);
        var manifest = ReadManifest(folder.Source, folder.IconScope + folder.Path);
        var hidden = new HashSet<string>(manifest?.Hidden ?? [], StringComparer.Ordinal);
        var entries = new Dictionary<string, Node>(NameComparer);
        foreach (var item in listing.Folders ?? [])
            AddRemote(item, true);
        foreach (var item in listing.Files ?? [])
            AddRemote(item, false);

        AddFolderIcon(folder, entries);
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
    }

    private void AddFolderIcon(Node folder, Dictionary<string, Node> entries)
    {
        if (folder.Icon is { } choice)
        {
            try
            {
                var image = icons.Load(choice, api);
                var name = FolderIcons.FileName(choice);
                if (!entries.ContainsKey(name))
                {
                    entries.Add(name, Synthetic(name, image));
                    entries.TryAdd("desktop.ini", Synthetic("desktop.ini", FolderIcons.DesktopIni(choice)));
                }
            }
            catch (Exception error) when (error is IOException or HttpRequestException or NotSupportedException
                or FileFormatException or OperationCanceledException)
            {
                // An unavailable icon must never prevent browsing the directory.
            }
        }

        Node Synthetic(string name, byte[] bytes) => new(name, folder.Source, "", ChildPath(folder, name),
            false, bytes.Length, DateTimeOffset.UnixEpoch, true, null, bytes);
    }

    private Node[] ListSources()
    {
        var scopes = api.GetAccount().Scopes ?? [];
        var homeSource = scopes.FirstOrDefault(scope => scope.Name == "Home");
        string homeScope = homeSource?.Scope?.Trim('/') ?? "";
        var home = homeSource is not null && homeScope.Length == 0
            ? ReadManifest("Home", "") : null;
        var sources = scopes.Where(scope => scope.Name != "Home" && ValidName(scope.Name))
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
            .ToList();

        if ((home?.Storage?.ContainsKey(DevicesFolder) == true || homeScope == DevicesFolder ||
             homeScope.StartsWith(DevicesFolder + "/", StringComparison.Ordinal)) &&
            !sources.Any(source => NameComparer.Equals(source.Name, DevicesFolder)))
        {
            sources.Add(new Node(DevicesFolder, "Home", "/", ChildPath(root, DevicesFolder), true, 0,
                DateTimeOffset.UnixEpoch, false, FindIcon(home, DevicesFolder) ?? new IconChoice("devices", null),
                IconScope: homeScope, IsDeviceGroup: true));
        }

        if (hasRecycleBin && !sources.Any(source => NameComparer.Equals(source.Name, RecycleBinShortcut)))
        {
            string icon = System.IO.Path.Combine(Environment.SystemDirectory, "imageres.dll");
            byte[] shortcut = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(
                $"[InternetShortcut]\r\nURL={api.RecycleBinPage.AbsoluteUri}\r\nIconFile={icon}\r\nIconIndex=-55\r\n")).ToArray();
            sources.Add(new Node(RecycleBinShortcut, "", "", ChildPath(root, RecycleBinShortcut), false, shortcut.Length,
                DateTimeOffset.UnixEpoch, false, null, shortcut, VisibleShortcut: true));
        }

        return sources.OrderBy(node => node.Name, NameComparer).ToArray();
    }

    internal void RefreshListings() => directories.Clear();

    private Node[] ListDevices(Node folder)
    {
        ConnectedDevice[] devices;
        try { devices = api.GetConnectedDevices(); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            devices = [];
        }

        var entries = new Dictionary<string, Node>(NameComparer);
        foreach (var device in devices.Take(64))
        {
            if (device is not { Source: "Home", Status: "ready", Name: { } name, Path: { } path } ||
                !ValidName(name) || path != DevicesFolder + "/" + name)
                continue;

            string scope = folder.IconScope;
            if (scope.Length != 0 && path != scope && !path.StartsWith(scope + "/", StringComparison.Ordinal))
                continue;
            string relative = "/" + path[scope.Length..].TrimStart('/');
            var icon = device.IconUrl is { } url && url.StartsWith("/__foldericons/device-icon?", StringComparison.Ordinal)
                ? new IconChoice(null, url) : new IconChoice("usb", null);
            entries.TryAdd(name, new Node(name, "Home", relative, ChildPath(folder, name), true, 0,
                DateTimeOffset.UnixEpoch, false, icon, IconScope: scope));
        }
        AddFolderIcon(folder, entries);
        return entries.Values.OrderBy(node => node.Name, NameComparer).ToArray();
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

    private FileInformation Info(Node item)
    {
        DateTime time = item.Modified.Year < 1601 ? DateTime.UnixEpoch : item.Modified.UtcDateTime;
        return new FileInformation
        {
            FileName = item.Name,
            Attributes = !item.Directory && IsWritable(item)
                ? item.Hidden ? FileAttributes.Hidden : FileAttributes.Normal
                : item.Attributes,
            Length = item.Directory ? 0 : item.Length,
            CreationTime = time,
            LastWriteTime = time,
            LastAccessTime = time
        };
    }
}
