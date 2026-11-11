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

internal static class WriteTests
{
    internal static void Run()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var fixture = new WriteServer();
        using var api = new FilePointApi(new Uri("https://filepoint.example"), fixture);
        api.UseToken("fixture.header.signature");
        var recovery = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var mount = new DriveMount(new FilePointDrive(api, new FolderIcons(), identity.User!, api.GetWritableSources(),
            path => recovery.TrySetResult(path)));
        string root = Path.Combine(mount.Path, "Documents");
        string local = Path.GetTempFileName();
        try
        {
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

            Directory.CreateDirectory(Path.Combine(root, "New folder"));
            Require(fixture.Folders.ContainsKey("/New folder"), "Create a folder");
            File.Move(Path.Combine(root, "copied.bin"), Path.Combine(root, "renamed.bin"));
            Require(!fixture.Files.ContainsKey("/copied.bin") && fixture.Files["/renamed.bin"].SequenceEqual(copy),
                "Rename a file without changing its data");
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
            Console.WriteLine("Passed writable mount, multi-chunk copy, file editing, folder creation, and conflict recovery.");
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (uri.AbsolutePath == "/api/users")
                return Json(new { username = "Test", scopes = new[] { new { name = "Documents", scope = "/" } } });
            if (uri.AbsolutePath == "/api/resources")
                return query["path"] == "/"
                    ? Json(new
                    {
                        files = Files.Select(file => new { name = file.Key[1..], size = file.Value.Length }),
                        folders = Folders.Keys.Select(folder => new { name = folder[1..] })
                    })
                    : Json(new { });
            if (uri.AbsolutePath == "/api/resources/download" && query["file"] is { } name && Files.TryGetValue(name, out var bytes))
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
                    return Json(new { protocol = 1, sources = new[] { "Documents" } });
                if (request.Method == HttpMethod.Post)
                {
                    string path = query["path"]!;
                    if (query["mode"] == "directory")
                    {
                        Folders.TryAdd(path, 0);
                        return Json(new { created = true }, HttpStatusCode.Created);
                    }
                    if (query["mode"] == "rename")
                    {
                        string destination = query["destination"]!;
                        if (!Files.TryGetValue(path, out var moved))
                            return new(HttpStatusCode.NotFound);
                        if (!Files.TryAdd(destination, moved))
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
