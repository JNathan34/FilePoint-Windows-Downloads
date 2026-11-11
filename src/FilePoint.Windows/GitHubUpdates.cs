using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FilePoint.Windows;

internal sealed record GitHubUpdate(Version Version, string AssetName, long Size, string Sha256);

internal sealed class GitHubUpdates : IDisposable
{
    private const string Repository = "JNathan34/FilePoint-Windows-Downloads";
    private const string ReleasePage = "https://github.com/" + Repository + "/releases";
    internal const string ReleasesUrl = ReleasePage + "/latest";
    private const string ApiUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly Architecture architecture;

    private sealed record Release([property: JsonPropertyName("tag_name")] string? TagName,
        bool Draft, bool Prerelease, Asset[]? Assets);
    private sealed record Asset(string? Name, string? State, long Size, string? Digest);

    internal GitHubUpdates(HttpMessageHandler? handler = null, Architecture? architecture = null)
    {
        this.architecture = architecture ?? RuntimeInformation.OSArchitecture;
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FilePoint-Windows-Updater");
    }

    internal static Version CurrentVersion
    {
        get
        {
            var version = typeof(GitHubUpdates).Assembly.GetName().Version!;
            return new Version(version.Major, version.Minor, version.Build);
        }
    }

    internal async Task<GitHubUpdate?> CheckAsync(Version current, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var data = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<Release>(data, Json, cancellation).ConfigureAwait(false);
        if (release is null || release.Draft || release.Prerelease ||
            release.TagName is not { } tag || !tag.StartsWith('v') ||
            !Version.TryParse(tag[1..], out var version) || version.Build < 0 || version.Revision != -1 ||
            tag != $"v{version}")
            throw new InvalidDataException("GitHub returned an invalid stable FilePoint release.");
        if (version <= current)
            return null;

        string setupName = architecture switch
        {
            Architecture.Arm64 => "FilePoint-Setup-arm64.exe",
            Architecture.X64 => "FilePoint-Setup-x64.exe",
            _ => throw new PlatformNotSupportedException("FilePoint updates require Windows x64 or ARM64.")
        };
        var assets = release.Assets?.Where(asset => asset.Name == setupName && asset.State == "uploaded").ToArray();
        if (assets is not { Length: 1 } || assets[0] is not { Size: > 0 and <= 512 * 1024 * 1024 } setup ||
            setup.Digest is not { Length: 71 } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !digest.AsSpan(7).ToString().All(Uri.IsHexDigit))
            throw new InvalidDataException($"The GitHub release is missing a verifiable {setupName} installer.");

        return new(version, setupName, setup.Size, digest[7..]);
    }

    internal async Task<string> DownloadAsync(GitHubUpdate update, CancellationToken cancellation, IProgress<int>? progress = null)
    {
        string directory = Directory.CreateTempSubdirectory("FilePoint-Update-").FullName;
        // WiX Burn uses the executable's name when caching it for repair and later upgrades.
        string installer = Path.Combine(directory, update.AssetName);
        string partial = installer + ".part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ReleasePage}/download/v{update.Version}/{update.AssetName}");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Redirect)
            {
                var location = response.Headers.Location;
                if (location is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true } ||
                    location.UserInfo.Length != 0 ||
                    location.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("GitHub returned an unexpected download address.");

                using var download = new HttpRequestMessage(HttpMethod.Get, location);
                using var bytes = await client.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellation)
                    .ConfigureAwait(false);
                await WriteVerifiedAsync(bytes, update, partial, cancellation, progress).ConfigureAwait(false);
            }
            else
            {
                await WriteVerifiedAsync(response, update, partial, cancellation, progress).ConfigureAwait(false);
            }

            File.Move(partial, installer);
            // Retain the normal Windows checks that also apply to installers downloaded in a browser.
            File.WriteAllText(installer + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            return installer;
        }
        catch
        {
            File.Delete(partial);
            File.Delete(installer);
            Directory.Delete(directory);
            throw;
        }
    }

    private static async Task WriteVerifiedAsync(HttpResponseMessage response, GitHubUpdate update,
        string path, CancellationToken cancellation, IProgress<int>? progress)
    {
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentLength is { } length && length != update.Size)
            throw new InvalidDataException("GitHub returned the wrong installer size.");

        await using var data = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long written = 0;
        int lastPercent = 0;
        int read;
        while ((read = await data.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            written += read;
            if (written > update.Size)
                throw new InvalidDataException("GitHub returned the wrong installer size.");
            hash.AppendData(buffer, 0, read);
            await file.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
            int percent = (int)(written * 100 / update.Size);
            if (progress is not null && percent != lastPercent)
            {
                progress.Report(percent);
                lastPercent = percent;
            }
        }

        if (written != update.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installer failed the GitHub SHA-256 verification.");
    }

    public void Dispose() => client.Dispose();
}
