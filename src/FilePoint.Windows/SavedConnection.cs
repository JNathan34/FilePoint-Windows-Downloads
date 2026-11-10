using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace FilePoint.Windows;

internal sealed record SavedConnection(string Address, string Token)
{
    private static readonly string PathName = Path.Combine(ExplorerIntegration.DataPath, "connection.bin");

    internal static SavedConnection? Load()
    {
        if (!File.Exists(PathName))
            return null;
        return JsonSerializer.Deserialize<SavedConnection>(
            ProtectedData.Unprotect(File.ReadAllBytes(PathName), null, DataProtectionScope.CurrentUser));
    }

    internal void Save()
    {
        Directory.CreateDirectory(ExplorerIntegration.DataPath);
        string temporary = PathName + ".tmp";
        File.WriteAllBytes(temporary, ProtectedData.Protect(
            JsonSerializer.SerializeToUtf8Bytes(this), null, DataProtectionScope.CurrentUser));
        File.Move(temporary, PathName, overwrite: true);
    }

    internal static void Forget()
    {
        if (!Directory.Exists(ExplorerIntegration.DataPath))
            return;

        File.Delete(PathName);
        File.Delete(PathName + ".tmp");
    }
}
