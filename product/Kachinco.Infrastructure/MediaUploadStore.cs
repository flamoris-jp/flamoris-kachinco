using System.Globalization;
using System.Security.Cryptography;
using Kachinco.Core;
using Flamoris.Mcp.Core;

namespace Kachinco.Infrastructure;

// One attachment owns unfinished bytes. Only the ordinary editor owns assets/history.
public sealed class MediaUploadStore(string root, IMediaProbe probe, TimeProvider? clock = null, string? projectDirectory = null) : IDisposable
{
    public const long MaximumFileBytes = 512L * 1024 * 1024;
    public const int MaximumChunkBytes = 128 * 1024;
    public const int MaximumBase64Chars = (MaximumChunkBytes + 2) / 3 * 4;
    private readonly object gate = new();
    private readonly string mediaRoot = Path.GetFullPath(root);
    private readonly string stagingId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<Guid, Upload> uploads = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private ITimer? expiry;
    private bool disposed;
    private string StagingRoot => Path.Combine(mediaRoot, ".uploads", stagingId);

    private sealed class Upload(Guid id, string name, long size, string digest, string path, DateTimeOffset touched)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public long Size { get; } = size;
        public string Digest { get; } = digest;
        public string Path { get; } = path;
        public long Received { get; set; }
        public DateTimeOffset Touched { get; set; } = touched;
        public bool Preparing { get; set; }
        public Guid Reservation { get; } = Guid.NewGuid();
    }

    public sealed record Receipt(Guid UploadId, string ReceivedBytes, string SizeBytes, int ChunkBytes);
    public sealed record Prepared(Guid UploadId, MediaProbeInfo Info, Guid Reservation);
    public sealed record Imported(bool Success, string Revision, Guid MediaAssetId, string Name,
        string Kind, string DurationTicks, string SizeBytes, string Sha256);

    public Receipt Begin(Guid id, string name, long size, string sha256, ProjectSnapshot snapshot)
    {
        ValidateName(name);
        if (id == Guid.Empty || size is < 1 or > MaximumFileBytes || !DigestValid(sha256))
            throw new McpFault("invalid_upload");
        lock (gate)
        {
            Demand(); Expire();
            if (uploads.TryGetValue(id, out var existing))
            {
                if (existing.Name != name || existing.Size != size || existing.Digest != sha256)
                    throw new McpFault("upload_conflict");
                existing.Touched = time.GetUtcNow();
                return Describe(existing);
            }
            if (uploads.Count >= 2) throw new McpFault("upload_limit");
            string final = FinalPath(id, name);
            if (snapshot.Project!.Assets.Any(a => a.Id == id) || File.Exists(final))
                throw new McpFault("upload_conflict");
            EnsureDirectory(mediaRoot);
            EnsureDirectory(Path.Combine(mediaRoot, ".uploads"));
            EnsureDirectory(StagingRoot);
            string path = Path.Combine(StagingRoot, id.ToString("N") + Path.GetExtension(name).ToLowerInvariant());
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            var upload = new Upload(id, name, size, sha256, path, time.GetUtcNow());
            uploads.Add(id, upload);
            expiry ??= time.CreateTimer(_ => { lock (gate) { if (!disposed) Expire(); } }, null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            return Describe(upload);
        }
    }

    public Receipt Write(Guid id, long offset, string dataBase64, string chunkSha256)
    {
        if (offset < 0 || dataBase64.Length is < 1 or > MaximumBase64Chars || !DigestValid(chunkSha256))
            throw new McpFault("invalid_chunk");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(dataBase64); }
        catch (FormatException) { throw new McpFault("invalid_chunk"); }
        if (bytes.Length is < 1 or > MaximumChunkBytes || Convert.ToBase64String(bytes) != dataBase64 ||
            Hash(bytes) != chunkSha256) throw new McpFault("invalid_chunk");
        lock (gate)
        {
            Demand(); Expire();
            var upload = Get(id);
            if (upload.Preparing) throw new McpFault("upload_busy");
            if (offset > upload.Received || offset > upload.Size - bytes.Length)
                throw new McpFault("upload_offset");
            using var stream = new FileStream(upload.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.Position = offset;
            if (offset < upload.Received)
            {
                if (bytes.Length > upload.Received - offset) throw new McpFault("upload_offset");
                byte[] previous = new byte[bytes.Length]; stream.ReadExactly(previous);
                if (!previous.AsSpan().SequenceEqual(bytes)) throw new McpFault("upload_conflict");
            }
            else
            {
                try { stream.Write(bytes); stream.Flush(); }
                catch { stream.SetLength(upload.Received); throw; }
                upload.Received += bytes.Length;
            }
            upload.Touched = time.GetUtcNow();
            return Describe(upload);
        }
    }

    public async Task<Prepared> PrepareAsync(Guid id, CancellationToken token)
    {
        Upload upload;
        lock (gate)
        {
            Demand(); Expire(); upload = Get(id);
            if (upload.Preparing) throw new McpFault("upload_busy");
            if (upload.Received != upload.Size) throw new McpFault("upload_incomplete");
            upload.Preparing = true;
            upload.Touched = time.GetUtcNow();
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using (var file = new FileStream(upload.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                string digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellation.Token));
                if (file.Length != upload.Size || digest != upload.Digest) throw new McpFault("upload_digest_mismatch");
            }
            var inspected = await probe.ProbeAsync(upload.Path, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!inspected.Success) throw new McpFault("upload_media_invalid");
            return new(id, inspected.Value!, upload.Reservation);
        }
        catch (McpFault)
        {
            lock (gate) { Remove(upload); }
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lock (gate) { Remove(upload); }
            throw new McpFault("upload_storage_unavailable");
        }
        finally
        {
            lock (gate)
            {
                upload.Preparing = false;
                if (disposed) { Remove(upload); DeleteEmptyStaging(); }
            }
        }
    }

    // Must run once inside RequestContext.CommitAsync after asynchronous preparation.
    public Imported Finish(Prepared prepared, EditorSession session, CancellationToken token)
    {
        lock (gate)
        {
            Demand(); var upload = Get(prepared.UploadId);
            if (upload.Preparing) throw new McpFault("upload_busy");
            if (upload.Reservation != prepared.Reservation || upload.Received != upload.Size || prepared.Info.SourcePath != upload.Path)
                throw new McpFault("upload_conflict");
            token.ThrowIfCancellationRequested();
            string final = FinalPath(upload.Id, upload.Name);
            EnsureDirectory(mediaRoot);
            if (File.Exists(final)) throw new McpFault("upload_conflict");
            File.Move(upload.Path, final);
            try
            {
                var asset = (prepared.Info with { SourcePath = projectDirectory is null ? final : Path.GetRelativePath(projectDirectory, final) })
                    .ToMediaAsset(upload.Id, upload.Name);
                var result = session.Execute(new([new RegisterMedia(asset)], session.GetProject().Revision), token);
                if (!result.Success) throw new McpFault("upload_registration_failed");
                uploads.Remove(upload.Id);
                return new(true, result.Revision.ToString(CultureInfo.InvariantCulture), asset.Id, asset.Name,
                    asset.Kind.ToString(), asset.DurationTicks.ToString(CultureInfo.InvariantCulture),
                    upload.Size.ToString(CultureInfo.InvariantCulture), upload.Digest);
            }
            catch { File.Move(final, upload.Path); throw; }
        }
    }

    public bool Cancel(Guid id)
    {
        lock (gate)
        {
            Demand();
            if (!uploads.TryGetValue(id, out var upload)) return false;
            if (upload.Preparing) throw new McpFault("upload_busy");
            Remove(upload); return true;
        }
    }

    private string FinalPath(Guid id, string name) =>
        Path.Combine(mediaRoot, id.ToString("N") + Path.GetExtension(name).ToLowerInvariant());
    private static Receipt Describe(Upload u) => new(u.Id, u.Received.ToString(CultureInfo.InvariantCulture),
        u.Size.ToString(CultureInfo.InvariantCulture), MaximumChunkBytes);
    private Upload Get(Guid id) => uploads.TryGetValue(id, out var upload) ? upload : throw new McpFault("upload_not_found");
    private void Demand() { if (disposed) throw new McpFault("upload_closed"); }
    public static bool DigestValid(string text) => text.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name != name.Trim() ||
            name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Any(char.IsControl) ||
            !MediaSourceFormats.TryGetKind(name, out _)) throw new McpFault("invalid_upload_name");
    }
    private static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new McpFault("upload_storage_unavailable");
    }
    private void Expire()
    {
        foreach (var upload in uploads.Values.Where(u => !u.Preparing && time.GetUtcNow() - u.Touched > TimeSpan.FromMinutes(30)).ToArray())
            Remove(upload);
    }
    private void Remove(Upload upload)
    {
        try { File.Delete(upload.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        uploads.Remove(upload.Id);
    }
    private void DeleteEmptyStaging()
    {
        try { Directory.Delete(StagingRoot); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        expiry?.Dispose();
        lifetime.Cancel();
        lock (gate)
        {
            foreach (var upload in uploads.Values.ToArray()) if (!upload.Preparing) Remove(upload);
            DeleteEmptyStaging();
        }
    }
}
