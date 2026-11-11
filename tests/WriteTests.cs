using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using FilePoint.Windows;
using Microsoft.VisualBasic.FileIO;

internal static class WriteTests
{
    internal static void Run()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var fixture = new WriteServer();
        using var api = new FilePointApi(new Uri("https://filepoint.example"), fixture);
        api.UseToken("fixture.header.signature");
        var recovery = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedDelete = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var account = api.GetAccount();
        Require(!(account with { Permissions = new AccountPermissions(false) }).CanDelete,
            "Do not enable deletion for accounts without permission");
        fixture.RecycleAvailable = false;
        Require(!api.SupportsRecycleBin(), "Do not enable deletion on a server without the recycle service");
        fixture.RecycleAvailable = true;
        Require(api.SupportsRecycleBin() && account.CanDelete, "Enable deletion on a server with the recycle service");
        var fileSystem = new FilePointDrive(api, new FolderIcons(), identity.User!, api.GetWritableSources(),
            path => recovery.TrySetResult(path), canRecycle: true, recycleFailed: name => failedDelete.TrySetResult(name),
            hasRecycleBin: true);
        using var mount = new DriveMount(fileSystem);
        string root = Path.Combine(mount.Path, "Documents");
        string local = Path.GetTempFileName();
        try
        {
            string shortcut = Path.Combine(mount.Path, "Recycle Bin.url");
            Require(Directory.EnumerateFiles(mount.Path).Contains(shortcut) &&
                !File.GetAttributes(shortcut).HasFlag(FileAttributes.Hidden) &&
                File.ReadAllText(shortcut).Contains("URL=https://filepoint.example/?plexpointRecycleBin=1", StringComparison.Ordinal),
                "Explorer lists a visible shortcut to the connected server's Recycle Bin");
            byte[] copy = new byte[5 * 1024 * 1024 + 23];
            new Random(7).NextBytes(copy);
            File.WriteAllBytes(local, copy);
            File.Copy(local, Path.Combine(root, "copied.bin"));
            Await(() => fixture.Files.TryGetValue("/copied.bin", out var bytes) && bytes.SequenceEqual(copy),
                "Copy a file spanning multiple upload chunks");

            using (var edit = File.Open(Path.Combine(root, "existing.txt"), FileMode.Open, FileAccess.ReadWrite))
            {
                Require(edit.ReadByte() == 'a', "An edit starts with the original file contents");
                edit.WriteByte((byte)'Z');
                edit.Flush(flushToDisk: true);
                Require(Encoding.UTF8.GetString(fixture.Files["/existing.txt"]) == "aZc",
                    "Flush commits the edited contents");
                edit.WriteByte((byte)'!');
            }
            Await(() => Encoding.UTF8.GetString(fixture.Files["/existing.txt"]) == "aZ!", "Save again from the same handle");
            File.AppendAllText(Path.Combine(root, "existing.txt"), " appended");
            Await(() => Encoding.UTF8.GetString(fixture.Files["/existing.txt"]) == "aZ! appended", "Append to an existing file");
            string replacement = Path.Combine(root, "replacement.txt");
            File.WriteAllText(replacement, "replacement");
            Await(() => fixture.Files.ContainsKey("/replacement.txt"), "Stage replacement contents");
            string relocated = Path.Combine(root, "relocated.txt");
            File.Move(replacement, relocated, overwrite: true);
            Require(fixture.Files.ContainsKey("/relocated.txt"), "Move with overwrite enabled to an unused name");
            File.Move(relocated, Path.Combine(root, "EXISTING.TXT"), overwrite: true);
            Require(Encoding.UTF8.GetString(fixture.Files["/existing.txt"]) == "replacement" &&
                !fixture.Files.ContainsKey("/EXISTING.TXT"), "Replace an existing file using a different case");

            Directory.CreateDirectory(Path.Combine(root, "New folder"));
            Require(fixture.Folders.ContainsKey("/New folder"), "Create a folder");
            File.Move(Path.Combine(root, "copied.bin"), Path.Combine(root, "renamed.bin"));
            Require(!fixture.Files.ContainsKey("/copied.bin") && fixture.Files["/renamed.bin"].SequenceEqual(copy),
                "Rename a file without changing its data");
            string renamed = Path.Combine(root, "renamed.bin");
            FileSystem.DeleteFile(renamed, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            Await(() => fixture.Recycled.TryGetValue("/renamed.bin", out var recycled) && recycled.SequenceEqual(copy),
                "Explorer drive deletes move the original bytes to the server recycle bin");
            Require(!Directory.EnumerateFiles(root).Contains(renamed), "A recycled file disappears from the mounted listing");
            var item = api.ListRecycledItems().Single();
            Require(item.Name == "renamed.bin" && item.Path == "/renamed.bin" && item.Source == "Documents",
                "List the deleted file and its original location");
            api.ChangeRecycledItem(item.Id, restore: true);
            fileSystem.RefreshListings();
            Require(api.ListRecycledItems().Length == 0 && fixture.Files["/renamed.bin"].SequenceEqual(copy),
                "Restore by recycle-bin ID and preserve the original bytes");
            File.Delete(renamed);
            Await(() => fixture.Recycled.ContainsKey("/renamed.bin"), "Recycle the restored file");
            api.ChangeRecycledItem(api.ListRecycledItems().Single().Id, restore: false);
            Require(api.ListRecycledItems().Length == 0 && !fixture.Files.ContainsKey("/renamed.bin"),
                "Permanently delete the selected recycle-bin entry");
            Directory.Delete(Path.Combine(root, "New folder"));
            Await(() => !fixture.Folders.ContainsKey("/New folder"), "Delete an empty directory on the mounted drive");
            string temporary = Path.Combine(root, "temporary.txt");
            File.WriteAllText(temporary, "delete on close");
            Await(() => fixture.Files.ContainsKey("/temporary.txt"), "Create a file for delete-on-close");
            using (new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.DeleteOnClose))
                Require(fixture.Files.ContainsKey("/temporary.txt"), "Wait until the delete-on-close handle closes");
            Await(() => fixture.Recycled.ContainsKey("/temporary.txt"), "Recycle the file when the delete-on-close handle closes");
            string folder = Path.Combine(root, "Project");
            Directory.CreateDirectory(folder);
            string notes = Path.Combine(folder, "notes.txt");
            File.WriteAllText(notes, "project notes");
            Await(() => fixture.Files.ContainsKey("/Project/notes.txt"), "Create a file in a subfolder");
            Require(!Directory.EnumerateFiles(folder).Any(path => Path.GetFileName(path) == "desktop.ini"),
                "Shell copy and delete enumeration skips the generated desktop.ini");
            Require(File.Exists(Path.Combine(folder, "desktop.ini")), "Explorer can still open generated folder icon metadata");
            string copied = Path.Combine(root, "Project copy");
            FileSystem.CopyDirectory(folder, copied, UIOption.OnlyErrorDialogs);
            Await(() => fixture.Files.ContainsKey("/Project copy/notes.txt"), "Copy a populated folder in the Shell");
            string renamedFolder = Path.Combine(root, "Renamed project");
            Directory.Move(copied, renamedFolder);
            Require(fixture.Files.ContainsKey("/Renamed project/notes.txt"), "Rename a populated folder within the same source");
            string archive = Path.Combine(mount.Path, "Archive");
            FileSystem.MoveFile(notes, Path.Combine(archive, "notes.txt"), UIOption.OnlyErrorDialogs);
            Await(() => fixture.Files.ContainsKey("/@Archive/notes.txt") && !fixture.Files.ContainsKey("/Project/notes.txt"),
                "Move a file between two FilePoint sources in the Shell");
            FileSystem.MoveDirectory(renamedFolder, Path.Combine(archive, "Project copy"), UIOption.OnlyErrorDialogs);
            Await(() => fixture.Files.ContainsKey("/@Archive/Project copy/notes.txt") && !fixture.Folders.ContainsKey("/Renamed project"),
                "Move a populated folder between two FilePoint sources in the Shell");
            FileSystem.DeleteDirectory(Path.Combine(archive, "Project copy"), UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            Await(() => !fixture.Folders.ContainsKey("/@Archive/Project copy"), "Recycle a populated folder in the Shell");
            fixture.RejectRecycle = true;
            File.Delete(Path.Combine(root, "existing.txt"));
            Require(failedDelete.Task.WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult() == "existing.txt" &&
                fixture.Files.ContainsKey("/existing.txt"), "Report a recycle failure and preserve the server file");
            using (var conflict = File.Create(Path.Combine(root, "conflict.txt")))
            {
                conflict.Write("my changes"u8);
                fixture.Files["/conflict.txt"] = "other changes"u8.ToArray();
            }
            string kept = recovery.Task.WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
            Require(File.ReadAllText(kept) == "my changes" &&
                Encoding.UTF8.GetString(fixture.Files["/conflict.txt"]) == "other changes",
                "Keep the server copy and local edits on conflict");
            File.Delete(kept);
            Directory.Delete(Path.GetDirectoryName(kept)!);
            Console.WriteLine("Passed native file editing, replacement, folder copies, moves between sources, recycling, and conflict recovery.");
        }
        finally { File.Delete(local); }
    }

    private static void Await(Func<bool> complete, string description) =>
        Require(SpinWait.SpinUntil(complete, TimeSpan.FromSeconds(12)), description);

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }

    private sealed class WriteServer : HttpMessageHandler
    {
        private sealed record Staging(string Path, byte[]? Original, MemoryStream Data);
        private readonly ConcurrentDictionary<string, Staging> staging = new();
        internal ConcurrentDictionary<string, byte[]> Files { get; } = new()
        {
            ["/existing.txt"] = "abc"u8.ToArray()
        };
        internal ConcurrentDictionary<string, byte> Folders { get; } = new();
        internal ConcurrentDictionary<string, byte[]> Recycled { get; } = new();
        internal bool RecycleAvailable { get; set; } = true;
        internal bool RejectRecycle { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = HttpUtility.ParseQueryString(uri.Query);
            string prefix = query["source"] == "Archive" ? "/@Archive" : "";
            if (uri.AbsolutePath == "/api/users")
                return Json(new { username = "Test", permissions = new { delete = true, modify = true },
                    scopes = new[] { new { name = "Documents", scope = "/" }, new { name = "Archive", scope = "/" } } });
            if (uri.AbsolutePath == "/__foldericons/icons" && query["source"] == "Documents" && query["path"] == "")
                return Json(new { icons = new { Project = new { material = "folder" } } });
            if (uri.AbsolutePath == "/__foldericons/recycle-bin")
            {
                if (!RecycleAvailable)
                    return new(HttpStatusCode.NotFound);
                if (request.Method == HttpMethod.Get)
                {
                    var items = Recycled.Keys.Select(path => new
                    {
                        id = path, source = "Documents", path, name = Path.GetFileName(path), isDir = false,
                        deletedAt = DateTimeOffset.UnixEpoch
                    }).ToArray();
                    return Json(new { count = items.Length, items });
                }
                Require(request.Headers.Authorization?.Scheme == "Bearer", "Recycle Bin mutations use the signed-in session");
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                string id = body.RootElement.GetProperty("ids")[0].GetString()!;
                if (!Recycled.TryGetValue(id, out var payload))
                    return new(HttpStatusCode.NotFound);
                if (request.Method == HttpMethod.Post && !Files.TryAdd(id, payload))
                    return new(HttpStatusCode.Conflict);
                Recycled.TryRemove(id, out _);
                return Json(new { changed = true });
            }
            if (uri.AbsolutePath == "/api/resources" && request.Method == HttpMethod.Delete)
            {
                string path = query["path"]!;
                if (RejectRecycle)
                    return new(HttpStatusCode.Forbidden);
                if (Files.TryRemove(prefix + path, out var removed))
                    Recycled[prefix + path] = removed;
                else if (!Folders.TryRemove(prefix + path, out _))
                    return new(HttpStatusCode.NotFound);
                return Json(new { recycled = new[] { new { id = "recycled-item", source = query["source"], path } } });
            }
            if (uri.AbsolutePath == "/api/resources")
            {
                string directory = prefix + query["path"]!.TrimEnd('/') + "/";
                bool Child(string path) => path.StartsWith(directory, StringComparison.Ordinal)
                    && !path[directory.Length..].Contains('/') && path != "/@Archive";
                return Json(new
                {
                    files = Files.Where(file => Child(file.Key)).Select(file => new { name = file.Key[directory.Length..], size = file.Value.Length }),
                    folders = Folders.Keys.Where(Child).Select(folder => new { name = folder[directory.Length..] })
                });
            }
            if (uri.AbsolutePath == "/api/resources/download" && query["file"] is { } name && Files.TryGetValue(prefix + name, out var bytes))
            {
                if (request.Method == HttpMethod.Head)
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) { Headers = { ContentLength = bytes.Length } } };
                var range = request.Headers.Range!.Ranges.Single();
                int start = (int)range.From!.Value, end = (int)range.To!.Value;
                var content = new ByteArrayContent(bytes[start..(end + 1)]);
                content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
                return new(HttpStatusCode.PartialContent) { Content = content };
            }
            if (uri.AbsolutePath == "/__foldericons/native-files")
            {
                if (request.Method == HttpMethod.Get)
                    return Json(new { protocol = 1, sources = new[] { "Documents", "Archive" } });
                if (request.Method == HttpMethod.Post)
                {
                    string path = prefix + query["path"]!;
                    if (query["mode"] == "directory")
                    {
                        Folders.TryAdd(path, 0);
                        return Json(new { created = true }, HttpStatusCode.Created);
                    }
                    if (query["mode"] == "rename")
                    {
                        string destination = prefix + query["destination"]!;
                        if (Folders.ContainsKey(path))
                        {
                            if (!Folders.TryAdd(destination, 0))
                                return new(HttpStatusCode.Conflict);
                            foreach (var child in Files.Where(child => child.Key.StartsWith(path + "/", StringComparison.Ordinal)).ToArray())
                            {
                                Files[destination + child.Key[path.Length..]] = child.Value;
                                Files.TryRemove(child.Key, out _);
                            }
                            Folders.TryRemove(path, out _);
                            return Json(new { length = 0, revision = "directory" });
                        }
                        if (!Files.TryGetValue(path, out var moved))
                            return new(HttpStatusCode.NotFound);
                        bool updated = query["replace"] == "true"
                            ? Files.TryGetValue(destination, out var existing) && Files.TryUpdate(destination, moved, existing)
                            : Files.TryAdd(destination, moved);
                        if (!updated)
                            return new(HttpStatusCode.Conflict);
                        Files.TryRemove(path, out _);
                        return Json(new { length = moved.Length, revision = Revision(moved) });
                    }
                    Files.TryGetValue(path, out var original);
                    if ((query["mode"] == "edit") != (original is not null) ||
                        (query["revision"] is { Length: > 0 } prior && prior != Revision(original!)))
                        return new(HttpStatusCode.Conflict);
                    string upload = Guid.NewGuid().ToString("N");
                    staging.TryAdd(upload, new Staging(path, original, new MemoryStream()));
                    return Json(new { upload, length = original?.Length ?? 0, revision = original is null ? null : Revision(original) },
                        HttpStatusCode.Created);
                }
                string id = query["upload"]!;
                if (!staging.TryGetValue(id, out var pending))
                    return new(HttpStatusCode.NotFound);
                if (request.Method == HttpMethod.Put)
                {
                    Require(long.Parse(query["offset"]!) == pending.Data.Length, "Write chunks are sequential");
                    pending.Data.Write(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                    return Json(new { length = pending.Data.Length });
                }
                if (request.Method == HttpMethod.Delete)
                {
                    staging.TryRemove(id, out _);
                    pending.Data.Dispose();
                    return Json(new { cancelled = true });
                }
                if (request.Method == HttpMethod.Patch)
                {
                    Files.TryGetValue(pending.Path, out var current);
                    if (!ReferenceEquals(current, pending.Original))
                        return new(HttpStatusCode.Conflict);
                    Require(long.Parse(query["length"]!) == pending.Data.Length, "Only complete writes are committed");
                    byte[] data = pending.Data.ToArray();
                    Files[pending.Path] = data;
                    staging.TryRemove(id, out _);
                    pending.Data.Dispose();
                    return Json(new { length = data.Length, revision = Revision(data) });
                }
            }
            return new(HttpStatusCode.NotFound);
        }

        private static string Revision(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body)) };
    }
}
