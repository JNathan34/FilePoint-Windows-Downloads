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
    private long writtenPrefix;
    private long uploadedLength;
    private byte[]? chunk;
    private bool deferUpload;

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
            if (exists && !truncate)
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

    internal void Write(byte[] data, long offset, Action keepAlive)
    {
        dirty = true;
        Data.Position = offset;
        Data.Write(data);
        if (offset < uploadedLength)
        {
            // The server accepts sequential chunks. Replay from disk if an app rewrites one.
            TryCancel();
            deferUpload = true;
        }
        if (offset <= writtenPrefix)
            writtenPrefix = Math.Max(writtenPrefix, offset + data.Length);
        if (deferUpload || writtenPrefix - uploadedLength < ChunkSize)
            return;
        try
        {
            UploadThrough(writtenPrefix - writtenPrefix % ChunkSize, keepAlive);
        }
        catch (Exception error) when (IsTransferError(error))
        {
            // Finish staging locally. Save retries against the original revision.
            TryCancel();
            deferUpload = true;
        }
    }

    internal void Resize(long length)
    {
        dirty = true;
        Data.SetLength(length);
        writtenPrefix = Math.Min(writtenPrefix, length);
        if (length < uploadedLength)
        {
            TryCancel();
            deferUpload = true;
        }
    }

    internal void Save(Action keepAlive)
    {
        if (!dirty)
            return;
        Data.Flush(flushToDisk: true);
        try
        {
            if (upload is not null)
            {
                try { Commit(keepAlive); }
                catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // A handle can sit idle long enough for the server to expire its staging session.
                    TryCancel();
                    Commit(keepAlive);
                }
            }
            else
                Commit(keepAlive);
        }
        catch
        {
            TryCancel();
            throw;
        }
    }

    private void UploadThrough(long length, Action keepAlive)
    {
        upload ??= api.BeginWrite(source, path, revision is not null, revision);
        while (uploadedLength < length)
        {
            keepAlive();
            chunk ??= new byte[ChunkSize];
            int count = (int)Math.Min(chunk.Length, length - uploadedLength);
            Data.Position = uploadedLength;
            Data.ReadExactly(chunk.AsSpan(0, count));
            api.WriteChunk(upload.Upload, uploadedLength, chunk, count);
            uploadedLength += count;
        }
    }

    private void Commit(Action keepAlive)
    {
        UploadThrough(Data.Length, keepAlive);
        keepAlive();
        var committed = api.CommitWrite(upload!.Upload, Data.Length);
        upload = null;
        uploadedLength = 0;
        if (committed.Length != Data.Length || string.IsNullOrEmpty(committed.Revision))
            throw new IOException("FilePoint could not confirm the uploaded file.");
        revision = committed.Revision;
        deferUpload = false;
        dirty = false;
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
        uploadedLength = 0;
        if (upload is not { } pending)
            return;
        upload = null;
        try { api.CancelWrite(pending.Upload); }
        catch (Exception error) when (IsTransferError(error))
        {
            // The server expires incomplete staging sessions.
        }
    }

    private static bool IsTransferError(Exception error) =>
        error is HttpRequestException or IOException or JsonException or OperationCanceledException;

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
