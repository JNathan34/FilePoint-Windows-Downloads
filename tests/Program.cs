using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DokanNet;
using FilePoint.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var fixture = new Fixture();
        using var api = new FilePointApi(new Uri("https://filepoint.example"), fixture);
        try
        {
            api.Login("A + B", "p+%£", "");
            throw new InvalidOperationException("A verification challenge must request a code.");
        }
        catch (VerificationRequiredException) { }
        Require(api.Login("A + B", "p+%£", "123456") == "fixture.header.signature", "Password login");
        Require(api.GetAccount().Scopes[0].Name == "Test source", "Account scopes");
        Require(api.GetConnectedDevices().Any(device => device.Name == "USB Drive" && device.Status == "ready"),
            "Use the web app's authenticated device feed");
        Require(api.GetDiskSpace() == new DiskSpace(20_000_000_000, 7_000_000_000),
            "Capacity excludes folder aliases and sources outside the account");
        Require(DriveMount.PickPath(["C:\\"]) == "F:\\" && DriveMount.PickPath(["f:\\"]) == "P:\\",
            "F: is preferred with an available fallback");
        Require(api.List("Test source", "/").Folders?.Length == 2, "Listing");
        Require(api.List("Test source", "/").Files?[0].Size == 4096 && api.GetFileLength("Test source", "/file.txt") == 12,
            "File downloads report the true length rather than the listing's allocated size");
        Require(api.GetIcons("Home", "/").Icons?.ContainsKey("Test source") == true,
            "Home icon manifest uses an empty path");
        Require(api.GetIcons("Test source", "/My folder/").Icons?.ContainsKey("Nested folder") == true,
            "Nested icon manifest uses a source-relative path");
        Require(api.GetSourceRootIcon("Test source", "/")?.Url?.EndsWith("&v=test-root", StringComparison.Ordinal) == true,
            "Source ICO fallback uses the empty path and retains the icon version");
        Require(Encoding.ASCII.GetString(api.Read("Test source", "/file.txt", 12, 4, 4)) == "4567", "HTTP byte range");
        try
        {
            api.Read("Test source", "/file.txt", 13, 4, 4);
            throw new InvalidOperationException("Changed file length was accepted.");
        }
        catch (IOException) { }

        var icons = new FolderIcons();
        var choice = api.GetIcons("Test source", "/").Icons!["My folder"];
        byte[] generated = Task.Run(() => icons.Load(choice, api)).GetAwaiter().GetResult();
        using var image = new MemoryStream(generated);
        var decoder = new IconBitmapDecoder(image, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Require(decoder.Frames.Count == 4, "Multi-size Material icon");
        var ico = icons.Load(api.GetIcons("Test source", "/").Icons!["ICO folder"], api);
        Require(ico.SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "FilePoint.ico"))),
            "Original ICO bytes");
        Require(Encoding.Unicode.GetString(FolderIcons.DesktopIni(choice)).Contains(FolderIcons.FileName(choice)),
            "Explorer icon metadata");
        Console.WriteLine("Passed API authentication, file ranges, original ICOs and Material icon rendering.");
        UpdateTests.Run().GetAwaiter().GetResult();
        TestScopedDevices(icons);

        if (args.Contains("--native"))
        {
            TestNative(api, icons, fixture);
            WriteTests.Run();
        }
        SignInTests.Run(args);
    }

    private static void TestScopedDevices(FolderIcons icons)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var api = new FilePointApi(new Uri("https://filepoint.example"),
            new Fixture { HomeScope = "/Connected devices/USB Drive" });
        api.UseToken("fixture.header.signature");
        var drive = new FilePointDrive(api, icons, identity.User!);
        var info = new MockDokanFileInfo();
        Require(drive.FindFiles(@"\Connected devices\USB Drive", out var files, info) == DokanResult.Success &&
            files.Any(file => file.FileName == "portable.txt"), "Scoped devices use paths relative to Home");
        Require(drive.FindFiles(@"\Connected devices", out var devices, info) == DokanResult.Success &&
            !devices.Any(device => device.FileName == "Another USB"), "Do not display devices outside the Home scope");
    }

    private static void TestNative(FilePointApi api, FolderIcons icons, Fixture fixture)
    {
        if (Environment.GetEnvironmentVariable("FILEPOINT_ARCH") is { } architecture)
            Require(RuntimeInformation.ProcessArchitecture.ToString().Equals(architecture, StringComparison.OrdinalIgnoreCase),
                "Native filesystem tests must run on the target architecture");
        using var identity = WindowsIdentity.GetCurrent();
        using var host = new DriveMount(new FilePointDrive(api, icons, identity.User!));
        string mount = host.Path;
        var disk = new DriveInfo(mount);
        Require(disk.TotalSize == 20_000_000_000 && disk.AvailableFreeSpace == 7_000_000_000,
            "Windows receives server capacity");
        ExplorerIntegration.RegisterDriveIcon(mount);
        try { CheckShellIcon(mount, "FilePoint.ico"); }
        finally { ExplorerIntegration.UnregisterDriveIcon(mount); }
        string source = Path.Combine(mount, "Test source");
        Require(Directory.GetDirectories(mount).Length == 3 && !Directory.Exists(Path.Combine(mount, "Home")), "Drive scopes");
        string devices = Path.Combine(mount, "Connected devices");
        string usb = Path.Combine(devices, "USB Drive");
        Require(Directory.GetDirectories(devices).Length == 2 && !Directory.Exists(Path.Combine(devices, "Locked USB")),
            "Only ready devices appear in Explorer");
        CheckShellIcon(devices, FolderIcons.FileName(new IconChoice("devices", null)));
        CheckShellIcon(usb, FolderIcons.FileName(new IconChoice(null, "/__foldericons/device-icon?kind=usb")));
        Require(File.ReadAllText(Path.Combine(usb, "PORTABLE.txt")) == "0123456789AB", "Open a device file through Explorer");
        fixture.DeviceAttached = false;
        Require(SpinWait.SpinUntil(() => Directory.GetDirectories(devices).Length == 0, TimeSpan.FromSeconds(8)),
            "Devices disappear after the server reports disconnection and the folder refreshes");
        var rootIcon = api.GetIcons("Home", "").Icons!["Test source"];
        CheckShellIcon(source, FolderIcons.FileName(rootIcon));
        string scoped = Path.Combine(mount, "Scoped source");
        CheckShellIcon(scoped, FolderIcons.FileName(new IconChoice("hard_drive", null)));
        CheckShellIcon(Path.Combine(scoped, "Scoped child"), FolderIcons.FileName(new IconChoice("person", null)));
        Require(File.ReadAllText(Path.Combine(source, "FILE.txt")) == "0123456789AB",
            "File open through Windows when the listing reports allocated size");
        string folder = Path.Combine(source, "My folder");
        Require(File.GetAttributes(folder).HasFlag(FileAttributes.ReadOnly), "Explorer processes folder metadata");
        string ini = Path.Combine(folder, "desktop.ini");
        Require(File.GetAttributes(ini).HasFlag(FileAttributes.System), "Synthetic metadata attributes");
        var choice = new IconChoice("folder", null);
        Require(File.ReadAllText(ini).Contains(FolderIcons.FileName(choice)), "Synthetic desktop.ini contents");
        Require(File.ReadAllBytes(Path.Combine(folder, FolderIcons.FileName(choice))).Length > 100, "Synthetic ICO contents");
        CheckShellIcon(folder, FolderIcons.FileName(choice));
        CheckShellIcon(Path.Combine(folder, "Nested folder"), FolderIcons.FileName(new IconChoice("person", null)));
        var icoChoice = api.GetIcons("Test source", "/").Icons!["ICO folder"];
        CheckShellIcon(Path.Combine(source, "ICO folder"), FolderIcons.FileName(icoChoice));
        bool blocked = false;
        try { File.WriteAllText(Path.Combine(source, "blocked.txt"), "blocked"); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException) { blocked = true; }
        Require(blocked && !File.Exists(Path.Combine(source, "blocked.txt")), "Read-only mount");
        Console.WriteLine("Passed native mount, server capacity, file open, Shell icons, and read-only enforcement.");
    }

    private static void CheckShellIcon(string path, string expectedName)
    {
        const uint iconLocation = 0x00001000;
        var result = SHGetFileInfo(path, 0, out var fileInfo, (uint)Marshal.SizeOf<ShellFileInfo>(), iconLocation);
        Require(result != IntPtr.Zero && Path.GetFileName(fileInfo.DisplayName) == expectedName,
            $"Windows Shell icon for '{Path.GetFileName(path)}' returned '{fileInfo.DisplayName}'.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);

    private static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException(name);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        internal string HomeScope { get; init; } = "/";
        internal volatile bool DeviceAttached = true;

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/api/auth/login")
            {
                Require(request.Method == HttpMethod.Post && uri.Query == "?username=A%20%2B%20B", "Username encoding");
                Require(request.Headers.GetValues("X-Password").Single() == "p%2B%25%C2%A3", "Password encoding");
                if (!request.Headers.Contains("X-Secret"))
                    return new(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("""{"status":403,"message":"OTP code is required for user"}""")
                    };
                Require(request.Headers.GetValues("X-Secret").Single() == "123456", "Verification code");
                return Text("fixture.header.signature");
            }
            Require(request.Headers.Authorization?.Parameter == "fixture.header.signature", "Bearer authentication");
            return uri.AbsolutePath switch
            {
                "/api/users" => Text($$"""
                    {"username":"Test","scopes":[{"name":"Test source","scope":"/"},{"name":"Home","scope":"{{HomeScope}}"},
                    {"name":"Scoped source","scope":"/Team space"}]}
                    """),
                "/__foldericons/devices" => Text(DeviceAttached ? """
                    {"devices":[
                       {"name":"USB Drive","path":"Connected devices/USB Drive","source":"Home","status":"ready",
                        "iconUrl":"/__foldericons/device-icon?kind=usb"},
                       {"name":"Another USB","path":"Connected devices/Another USB","source":"Home","status":"ready"},
                       {"name":"Locked USB","path":"Connected devices/Locked USB","source":"Home","status":"locked"},
                       {"name":"Invalid device","path":"Documents/Invalid device","source":"Home","status":"ready"}]}
                    """ : """{"devices":[]}"""),
                "/api/resources" when HomeScope == "/" &&
                    uri.Query == "?source=Home&path=%2FConnected%20devices%2FUSB%20Drive" =>
                    Text("""{"files":[{"name":"portable.txt","size":4096}]}"""),
                "/api/resources" when HomeScope == "/Connected devices/USB Drive" &&
                    uri.Query == "?source=Home&path=%2F" => Text("""{"files":[{"name":"portable.txt","size":4096}]}"""),
                "/api/resources" when uri.Query == "?source=Test%20source&path=%2F" => Text("""
                    {"folders":[{"name":"My folder"},{"name":"ICO folder"}],"files":[{"name":"file.txt","size":4096}]}
                    """),
                "/api/resources" when uri.Query == "?source=Test%20source&path=%2FMy%20folder" =>
                    Text("""{"folders":[{"name":"Nested folder"}]}"""),
                "/api/resources" when uri.Query == "?source=Scoped%20source&path=%2F" =>
                    Text("""{"folders":[{"name":"Scoped child"}]}"""),
                "/api/resources" => Text("{}"),
                "/__foldericons/icons" when uri.Query.Contains("path=%2F", StringComparison.Ordinal) =>
                    new(HttpStatusCode.Forbidden),
                "/__foldericons/icons" when uri.Query == "?source=Home&path=" =>
                    Text("""
                        {"icons":{"Test source":{"url":"/__foldericons/icon?source=Home&path=Test%20source&v=home-test"}},
                         "storage":{"Test source":{"category":"drive","total":20000000000,"free":7000000000},
                                    "Scoped source":{"category":"folder","total":20000000000,"free":7000000000},
                                    "Other":{"category":"drive","total":10000000000,"free":10000000000},
                                    "Connected devices":{"category":"folder"}}}
                        """),
                "/__foldericons/icons" when uri.Query == "?source=Test%20source&path=" =>
                    Text("""{"icons":{"My folder":{"material":"folder"},"ICO folder":{"url":"/__foldericons/icon?id=fixture"}}}"""),
                "/__foldericons/icons" when uri.Query == "?source=Test%20source&path=My%20folder" =>
                    Text("""{"icons":{"Nested folder":{"material":"person"}}}"""),
                "/__foldericons/icons" when uri.Query == "?source=Scoped%20source&path=Team%20space" =>
                    Text("""{"icons":{"Scoped child":{"material":"person"}}}"""),
                "/__foldericons/icons" => Text("""{"icons":{}}"""),
                "/__foldericons/folder-icon" when uri.Query == "?source=Scoped%20source&path=Team%20space" =>
                    Text("""{"icon":"hard_drive"}"""),
                "/__foldericons/folder-icon" when uri.Query == "?source=Test%20source&path=" =>
                    Text("""{"icon":""}"""),
                "/__foldericons/icon" when request.Method == HttpMethod.Head && uri.Query == "?source=Test%20source&path=" =>
                    new(HttpStatusCode.OK) { Headers = { ETag = new EntityTagHeaderValue("\"test-root\"") } },
                "/__foldericons/icon" or "/__foldericons/device-icon" => new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "FilePoint.ico")))
                },
                "/api/resources/download" => Download(request),
                _ => new(HttpStatusCode.NotFound)
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        private static HttpResponseMessage Text(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

        private static HttpResponseMessage Download(HttpRequestMessage request)
        {
            Require(request.RequestUri!.Query is "?source=Test%20source&file=%2Ffile.txt" or
                "?source=Home&file=%2FConnected%20devices%2FUSB%20Drive%2Fportable.txt", "Download path encoding");
            if (request.Method == HttpMethod.Head)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([]) { Headers = { ContentLength = 12 } }
                };
            var range = request.Headers.Range!.Ranges.Single();
            int from = (int)range.From!.Value, to = (int)range.To!.Value;
            var content = new ByteArrayContent(Encoding.ASCII.GetBytes("0123456789AB")[from..(to + 1)]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, 12);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        }
    }
}
