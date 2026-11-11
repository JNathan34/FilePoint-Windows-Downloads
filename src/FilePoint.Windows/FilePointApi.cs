using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace FilePoint.Windows;

internal sealed record RemoteItem(string Name, long Size, DateTimeOffset Modified, bool Hidden);
internal sealed record Listing(RemoteItem[]? Folders, RemoteItem[]? Files);
internal sealed record IconChoice(string? Material, string? Url);
internal sealed record IconManifest(Dictionary<string, IconChoice>? Icons, string[]? Hidden,
    Dictionary<string, StorageVolume>? Storage);
internal sealed record StorageVolume(string? Category, long Total, long Free);
internal sealed record DiskSpace(long Total, long Free);
internal sealed record ConnectedDevice(string? Name, string? Path, string? Source, string? Status, string? IconUrl);
internal sealed record ConnectedDevices(ConnectedDevice[]? Devices);
internal sealed record SourceScope(string Name, string? Scope);
internal sealed record Account(string Username, SourceScope[] Scopes, bool Admin, AccountPermissions? Permissions)
{
    internal bool CanDelete => Admin || Permissions?.Delete == true;
    internal bool CanRestore => Admin || Permissions?.Modify == true;
}
internal sealed record AccountPermissions(bool Delete, bool Modify = false);
internal sealed record RecycleBin(int? Count, RecycledItem[]? Items);
internal sealed record RecycledItem(string Id, string Source, string Path, string Name, bool IsDir, DateTimeOffset DeletedAt);
internal sealed record RecycleResult(RecycledItem[]? Recycled);
internal sealed record RecycleChange(bool Changed, string? Message);
internal sealed record FolderIconSetting(string? Icon);
internal sealed record NativeWriteCapabilities(int Protocol, string[]? Sources);
internal sealed record NativeUpload(string Upload, long Length, string? Revision);
internal sealed record NativeCommit(long Length, string Revision);

internal sealed class VerificationRequiredException : Exception;

internal sealed class FilePointApi : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string NativeFiles = "/__foldericons/native-files";
    private const string RecycleEndpoint = "/__foldericons/recycle-bin";
    private readonly HttpClient client;
    internal Uri RecycleBinPage => new(client.BaseAddress!, "/?plexpointRecycleBin=1");

    internal FilePointApi(Uri address, HttpMessageHandler? handler = null)
    {
        if (address.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(address.UserInfo)
            || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("Enter the HTTPS address of your FilePoint server, without a path.");
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    internal string Login(string username, string password, string otp)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Query("/api/auth/login", ("username", username)));
        request.Headers.Add("X-Password", Uri.EscapeDataString(password));
        if (otp.Length != 0)
            request.Headers.Add("X-Secret", otp);
        using var response = client.Send(request);
        if (response.StatusCode == HttpStatusCode.Forbidden && otp.Length == 0 && RequiresVerification(response))
            throw new VerificationRequiredException();
        Check(response);
        var token = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
        UseToken(token);
        return token;
    }

    internal Account RestoreAccount(SavedConnection connection)
    {
        if (!string.Equals(connection.Address, client.BaseAddress!.AbsoluteUri, StringComparison.Ordinal))
            throw new ArgumentException("The saved sign-in belongs to a different server.");
        if (connection.Token is { Length: > 0 } token)
        {
            UseToken(token);
            try { return GetAccount(); }
            catch (HttpRequestException error) when (connection.HasPassword &&
                error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                client.DefaultRequestHeaders.Authorization = null;
            }
        }

        if (!connection.HasPassword)
            throw new InvalidDataException("FilePoint couldn’t read the saved sign-in.");
        Login(connection.Username!, connection.Password!, "");
        return GetAccount();
    }

    private static bool RequiresVerification(HttpResponseMessage response)
    {
        const string challenge = "OTP code is required for user";
        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
        if (body.Equals(challenge, StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String &&
                string.Equals(message.GetString(), challenge, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal void UseToken(string token)
    {
        if (token.Length > 8192 || token.Count(c => c == '.') != 2
            || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException("Enter a valid FilePoint API token.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    internal Account GetAccount() => Get<Account>("/api/users?id=self");
    internal Listing List(string source, string path) =>
        Get<Listing>(Query("/api/resources", ("source", source), ("path", path)));

    internal IconManifest GetIcons(string source, string path) =>
        Get<IconManifest>(IconQuery("/__foldericons/icons", source, path));

    internal ConnectedDevice[] GetConnectedDevices() => Get<ConnectedDevices>("/__foldericons/devices").Devices ?? [];

    internal string[] GetWritableSources()
    {
        try
        {
            var capabilities = Get<NativeWriteCapabilities>(NativeFiles);
            return capabilities.Protocol == 1 ? capabilities.Sources ?? [] : [];
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            return [];
        }
    }

    internal bool SupportsRecycleBin()
    {
        try { ListRecycledItems(); return true; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            return false;
        }
    }

    internal RecycledItem[] ListRecycledItems()
    {
        var bin = Get<RecycleBin>(RecycleEndpoint);
        if (bin.Items is not { } items || bin.Count != items.Length)
            throw new InvalidDataException("FilePoint returned an invalid Recycle Bin listing.");
        return items;
    }

    internal void ChangeRecycledItem(string id, bool restore)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { action = restore ? "restore" : "delete", ids = new[] { id } }),
            Encoding.UTF8, "application/json");
        var result = NativeRequest<RecycleChange>(restore ? HttpMethod.Post : HttpMethod.Delete, RecycleEndpoint, content);
        if (!result.Changed)
            throw new InvalidDataException("FilePoint could not confirm the Recycle Bin change.");
    }

    internal void Recycle(string source, string path)
    {
        var result = NativeRequest<RecycleResult>(HttpMethod.Delete,
            Query("/api/resources", ("source", source), ("path", path)));
        if (result.Recycled is not [{ Id.Length: > 0 } item] || item.Source != source || item.Path != path)
            throw new InvalidDataException("FilePoint could not confirm the item reached its recycle bin.");
    }

    internal NativeUpload BeginWrite(string source, string path, bool existing, string? revision = null) =>
        NativeRequest<NativeUpload>(HttpMethod.Post,
            Query(NativeFiles, ("source", source), ("path", path), ("mode", existing ? "edit" : "create"),
                ("revision", revision ?? "")));

    internal void CreateDirectory(string source, string path) =>
        NativeRequest<JsonElement>(HttpMethod.Post, Query(NativeFiles,
            ("source", source), ("path", path), ("mode", "directory")));

    internal NativeCommit Rename(string source, string path, string destination, bool replace) =>
        NativeRequest<NativeCommit>(HttpMethod.Post, Query(NativeFiles, ("source", source), ("path", path),
            ("mode", "rename"), ("destination", destination), ("replace", replace ? "true" : "false")));

    internal void WriteChunk(string upload, long offset, byte[] bytes, int count)
    {
        using var content = new ByteArrayContent(bytes, 0, count);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        NativeRequest<JsonElement>(HttpMethod.Put, Query(NativeFiles,
            ("upload", upload), ("offset", offset.ToString(CultureInfo.InvariantCulture))), content);
    }

    internal NativeCommit CommitWrite(string upload, long length) => NativeRequest<NativeCommit>(HttpMethod.Patch,
        Query(NativeFiles, ("upload", upload), ("length", length.ToString(CultureInfo.InvariantCulture))));

    internal void CancelWrite(string upload) => NativeRequest<JsonElement>(HttpMethod.Delete,
        Query(NativeFiles, ("upload", upload)));

    private T NativeRequest<T>(HttpMethod method, string path, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = client.Send(request);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"FilePoint could not complete the file operation (HTTP {(int)response.StatusCode}).",
                null, response.StatusCode);
        using var data = response.Content.ReadAsStream();
        return JsonSerializer.Deserialize<T>(data, Json) ?? throw new InvalidDataException("FilePoint returned an empty write response.");
    }

    internal DiskSpace? GetDiskSpace()
    {
        var scopes = GetAccount().Scopes;
        if (scopes is null || !scopes.Any(scope => scope.Name == "Home" && string.IsNullOrEmpty(scope.Scope?.Trim('/'))))
            return null;

        var storage = GetIcons("Home", "").Storage;
        if (storage is null)
            return null;

        var visible = scopes.Select(scope => scope.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var drives = storage.Where(item => visible.Contains(item.Key) &&
            item.Value is { Category: "drive", Total: > 0, Free: >= 0 } volume && volume.Free <= volume.Total)
            .DistinctBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Value).ToArray();
        return drives.Length == 0 ? null : new(drives.Sum(drive => drive.Total), drives.Sum(drive => drive.Free));
    }

    internal IconChoice? GetSourceRootIcon(string source, string path)
    {
        var setting = Get<FolderIconSetting>(IconQuery("/__foldericons/folder-icon", source, path));
        if (setting.Icon is { Length: > 0 } material)
            return new IconChoice(material, null);

        var url = IconQuery("/__foldericons/icon", source, path);
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = client.Send(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        Check(response);
        string? version = response.Headers.ETag?.Tag.Trim('"');
        return new IconChoice(null, string.IsNullOrEmpty(version) ? url : url + "&v=" + Uri.EscapeDataString(version));
    }

    internal byte[] GetIcon(string url)
    {
        if (!url.StartsWith("/__foldericons/icon?", StringComparison.Ordinal) &&
            !url.StartsWith("/__foldericons/device-icon?", StringComparison.Ordinal))
            throw new InvalidDataException("FilePoint returned an invalid icon link.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Check(response);
        const int maxSize = 8 * 1024 * 1024;
        using var data = response.Content.ReadAsStream(timeout.Token);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = data.ReadAsync(buffer.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult()) > 0)
        {
            if (result.Length + count > maxSize)
                throw new InvalidDataException("The folder icon is too large.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    internal long GetFileLength(string source, string path)
    {
        // The listing can report allocated disk blocks. The download reports the actual byte length.
        using var request = new HttpRequestMessage(HttpMethod.Head, DownloadUrl(source, path));
        using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
        Check(response);
        return response.Content.Headers.ContentLength is { } length
            ? length : throw new InvalidDataException("FilePoint did not report the file length.");
    }

    internal byte[] Read(string source, string path, long size, long offset, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl(source, path));
        request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
        using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Check(response);
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (range?.Unit != "bytes" || range.From != offset || range.To != offset + count - 1
                || range.Length != size)
                throw new IOException("The file changed during the download. Reopen it in Explorer.");
        }
        else if (offset != 0 || count != size || response.Content.Headers.ContentLength != size)
        {
            throw new IOException("FilePoint did not return the requested byte range.");
        }

        using var data = response.Content.ReadAsStream(timeout.Token);
        var bytes = new byte[count];
        data.ReadExactlyAsync(bytes, timeout.Token).AsTask().GetAwaiter().GetResult();
        return bytes;
    }

    private T Get<T>(string path)
    {
        using var response = client.GetAsync(path).GetAwaiter().GetResult();
        Check(response);
        using var data = response.Content.ReadAsStream();
        return JsonSerializer.Deserialize<T>(data, Json) ?? throw new InvalidDataException("FilePoint returned an empty response.");
    }

    private static string Query(string path, params (string Key, string Value)[] parameters) =>
        path + "?" + string.Join("&", parameters.Select(parameter =>
            parameter.Key + "=" + Uri.EscapeDataString(parameter.Value)));

    // FileBrowser resource paths start with a slash. The icon proxy takes source-relative paths
    // and identifies the virtual Home root specifically by an empty path.
    private static string IconQuery(string endpoint, string source, string path) =>
        Query(endpoint, ("source", source), ("path", path.Trim('/')));

    private static string DownloadUrl(string source, string path) =>
        Query("/api/resources/download", ("source", source), ("file", path));

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.PartialContent)
            return;
        string message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "FilePoint rejected the credentials or the session expired.",
            HttpStatusCode.Forbidden => "FilePoint denied access. Check the account, token permissions and any required verification code.",
            HttpStatusCode.NotFound => "FilePoint could not find this item.",
            HttpStatusCode.TooManyRequests => "FilePoint is limiting requests. Wait a moment and try again.",
            _ => $"FilePoint returned HTTP {(int)response.StatusCode}."
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    public void Dispose() => client.Dispose();
}
