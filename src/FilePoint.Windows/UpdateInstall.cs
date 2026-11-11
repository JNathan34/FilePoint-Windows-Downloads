using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace FilePoint.Windows;

internal static class UpdateInstall
{
    internal const string ReopenArgument = "--after-update";

    internal static void Start(string installer)
    {
        string executable = Environment.ProcessPath ?? throw new IOException("FilePoint could not locate its executable.");
        using var current = Process.GetCurrentProcess();
        var start = CreateStartInfo(installer, executable, current);
        using var helper = Process.Start(start) ?? throw new IOException("Windows did not start the update helper.");
    }

    internal static ProcessStartInfo CreateStartInfo(string installer, string executable, Process current)
    {
        // The helper must outlive FilePoint without keeping its executable open during replacement.
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $installer = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{Encode(installer)}}'))
            $application = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{Encode(executable)}}'))
            $previous = Get-Process -Id {{current.Id.ToString(CultureInfo.InvariantCulture)}} -ErrorAction SilentlyContinue
            if ($previous -and $previous.StartTime.ToUniversalTime().Ticks -eq {{current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}}) {
                try { Wait-Process -InputObject $previous -Timeout 60 -ErrorAction Stop }
                catch {
                    Add-Type -AssemblyName System.Windows.Forms
                    [void][Windows.Forms.MessageBox]::Show('Close FilePoint and try the update again.', 'FilePoint update')
                    exit 1
                }
            }
            $message = $null
            try {
                $setup = Start-Process -FilePath $installer -WorkingDirectory (Split-Path -LiteralPath $installer) -ArgumentList '/passive', '/norestart' -Wait -PassThru
                switch ($setup.ExitCode) {
                    0 {}
                    1602 {}
                    3010 { $message = 'Restart Windows to finish the FilePoint update.' }
                    default { $message = "FilePoint Setup returned error $($setup.ExitCode)." }
                }
            } catch {
                $message = 'Windows could not run FilePoint Setup.'
            }
            if ($message) {
                Add-Type -AssemblyName System.Windows.Forms
                [void][Windows.Forms.MessageBox]::Show($message, 'FilePoint update')
                if ($setup -and $setup.ExitCode -eq 3010) { exit 0 }
            }
            try {
                Start-Process -FilePath $application -WorkingDirectory (Split-Path -LiteralPath $application) -ArgumentList '{{ReopenArgument}}'
            } catch {
                Add-Type -AssemblyName System.Windows.Forms
                [void][Windows.Forms.MessageBox]::Show('Windows could not reopen FilePoint. Open it from the Start menu.', 'FilePoint update')
                exit 1
            }
            """;

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Encode(script));
        return start;
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
}
