using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FilePoint.Windows;

internal static class UpdateTests
{
    internal static async Task Run()
    {
        var fixture = new GitHubFixture();
        using var github = new GitHubUpdates(fixture, Architecture.X64);
        var update = await github.CheckAsync(new Version(1, 0, 0), CancellationToken.None);
        Require(update?.Version == new Version(1, 2, 0) && update.AssetName == "FilePoint-Setup-x64.exe", "x64 update selection");
        using var arm = new GitHubUpdates(new GitHubFixture(), Architecture.Arm64);
        Require((await arm.CheckAsync(new Version(1, 0, 0), CancellationToken.None))?.AssetName == "FilePoint-Setup-arm64.exe",
            "ARM64 update selection");
        Require(await github.CheckAsync(new Version(1, 2, 0), CancellationToken.None) is null, "Equal versions");
        Require(await github.CheckAsync(new Version(2, 0, 0), CancellationToken.None) is null, "Downgrade prevention");

        fixture.ReleaseTag = "v1.3.0-rc.1";
        await Reject<InvalidDataException>(() => github.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        fixture.ReleaseTag = "v1.2.0";

        string setup = await github.DownloadAsync(update!, CancellationToken.None);
        try
        {
            Require(Path.GetFileName(setup) == update!.AssetName, "Keep the installer name used by the WiX cache");
            Require((await File.ReadAllBytesAsync(setup)).AsSpan().SequenceEqual(GitHubFixture.Installer), "Verified setup bytes");
            Require(File.ReadAllText(setup + ":Zone.Identifier").Contains("ZoneId=3"), "Windows download marking");
        }
        finally
        {
            File.Delete(setup);
            Directory.Delete(Path.GetDirectoryName(setup)!);
        }

        await Reject<InvalidDataException>(() =>
            github.DownloadAsync(update! with { Sha256 = new string('0', 64) }, CancellationToken.None));
        fixture.Redirect = "https://untrusted.example/download";
        await Reject<InvalidDataException>(() => github.DownloadAsync(update!, CancellationToken.None));
        Console.WriteLine("Passed stable version checks, anonymous GitHub updates, trusted download redirects and SHA-256 verification.");
    }

    private static async Task Reject<T>(Func<Task> work) where T : Exception
    {
        try { await work(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException(name);
    }

    private sealed class GitHubFixture : HttpMessageHandler
    {
        internal static readonly byte[] Installer = [77, 90, 3, 4, 5];
        internal string ReleaseTag { get; set; } = "v1.2.0";
        internal string Redirect { get; set; } = "https://release-assets.githubusercontent.com/fixture?signature=test";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "release-assets.githubusercontent.com")
            {
                Require(request.Headers.Authorization is null, "Download redirects must be anonymous");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) });
            }

            Require(request.Headers.Authorization is null, "Update checks and downloads are anonymous.");

            if (uri.AbsolutePath == "/repos/JNathan34/FilePoint-Windows-Downloads/releases/latest")
            {
                Require(uri.Host == "api.github.com", "Unexpected GitHub API host");
                var digest = Convert.ToHexString(SHA256.HashData(Installer)).ToLowerInvariant();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""
                    {"tag_name":"{{ReleaseTag}}","draft":false,"prerelease":false,"assets":[
                      {"id":17,"name":"FilePoint-Setup-x64.exe","state":"uploaded","size":5,"digest":"sha256:{{digest}}"},
                      {"id":18,"name":"FilePoint-Setup-arm64.exe","state":"uploaded","size":5,"digest":"sha256:{{digest}}"}
                    ]}
                    """)
                });
            }

            Require(uri.AbsolutePath == "/JNathan34/FilePoint-Windows-Downloads/releases/download/v1.2.0/FilePoint-Setup-x64.exe" &&
                uri.Host == "github.com", "Public installer request");
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(Redirect);
            return Task.FromResult(response);
        }
    }
}
