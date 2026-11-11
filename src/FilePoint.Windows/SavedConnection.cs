using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace FilePoint.Windows;

internal sealed record SavedConnection(string Address, string? Token, string? Username = null, string? Password = null)
{
    internal bool HasPassword => !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password);
    private static readonly string PathName = Path.Combine(ExplorerIntegration.DataPath, "connection.bin");

    internal static SavedConnection? Load(string? path = null)
    {
        path ??= PathName;
        if (!File.Exists(path))
            return null;
        byte[] plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<SavedConnection>(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal void Save(string? path = null)
    {
        path ??= PathName;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(this);
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, path, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public override string ToString() => nameof(SavedConnection);

    internal static void Forget()
    {
        if (!Directory.Exists(ExplorerIntegration.DataPath))
            return;

        File.Delete(PathName);
        File.Delete(PathName + ".tmp");
    }
}
