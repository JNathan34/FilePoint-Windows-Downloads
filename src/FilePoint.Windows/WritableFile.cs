using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace FilePoint.Windows;

internal sealed class WritableFile : IDisposable
{
    private const int ChunkSize = 4 * 1024 * 1024;
    internal static string RecoveryPath => Path.Combine(ExplorerIntegration.DataPath, "Unsaved Uploads");
    private readonly FilePointApi api;
    private readonly string source;
    private string path;
    private NativeUpload? upload;
    private string? revision;
    private bool dirty;

    internal string LocalPath { get; }
    internal FileStream Data { get; }
    internal bool IsNew => revision is null;

    internal WritableFile(FilePointApi api, string source, string path, string name,
        bool exists, bool truncate, Action keepAlive)
    {
        this.api = api;
        this.source = source;
        this.path = path;
        upload = api.BeginWrite(source, path, exists);
        revision = upload.Revision;
        string folder = Path.Combine(RecoveryPath, Guid.NewGuid().ToString("N"));
        LocalPath = Path.Combine(folder, name);
        try
        {
            Directory.CreateDirectory(folder);
            Data = new FileStream(LocalPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            if (exists && !truncate)
            {
                long length = upload.Length;
                for (long offset = 0; offset < length;)
                {
                    keepAlive();
                    byte[] bytes = api.Read(source, path, length, offset, (int)Math.Min(1024 * 1024, length - offset));
                    Data.Write(bytes);
                    offset += bytes.Length;
                }
            }
            dirty = !exists || truncate;
            TryCancel();
        }
        catch
        {
            Data?.Dispose();
            TryCancel();
            RemoveLocalCopy();
            throw;
        }
    }

    internal void MarkChanged() => dirty = true;

    internal void Save(Action keepAlive)
    {
        if (!dirty)
            return;
        Data.Flush(flushToDisk: true);
        upload ??= api.BeginWrite(source, path, revision is not null, revision);
        try
        {
            byte[] buffer = new byte[ChunkSize];
            Data.Position = 0;
            while (Data.Position < Data.Length)
            {
                keepAlive();
                long offset = Data.Position;
                int count = (int)Math.Min(buffer.Length, Data.Length - offset);
                Data.ReadExactly(buffer.AsSpan(0, count));
                api.WriteChunk(upload.Upload, offset, buffer, count);
            }
            keepAlive();
            var committed = api.CommitWrite(upload.Upload, Data.Length);
            upload = null;
            if (committed.Length != Data.Length || string.IsNullOrEmpty(committed.Revision))
                throw new IOException("FilePoint could not confirm the uploaded file.");
            revision = committed.Revision;
            dirty = false;
        }
        catch
        {
            TryCancel();
            throw;
        }
    }

    internal void Discard() => dirty = false;

    internal void Renamed(string newPath, string newRevision)
    {
        TryCancel();
        path = newPath;
        revision = newRevision;
    }

    public void Dispose()
    {
        Data.Dispose();
        TryCancel();
        if (!dirty)
            RemoveLocalCopy();
    }

    private void TryCancel()
    {
        if (upload is not { } pending)
            return;
        upload = null;
        try { api.CancelWrite(pending.Upload); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            // The server expires incomplete staging sessions.
        }
    }

    private void RemoveLocalCopy()
    {
        try
        {
            File.Delete(LocalPath);
            Directory.Delete(Path.GetDirectoryName(LocalPath)!);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
