using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MCperfBooster.Tests;

/// <summary>Temp folders under the run directory, so nothing lands in the real %LOCALAPPDATA%.</summary>
internal static class TempFolders
{
    public static string Create(string prefix)
    {
        string path = Path.Combine(TestEnvironment.RunDirectory, $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The whole run directory is removed when the test process exits.
            Debug.WriteLine($"Could not delete {path}: {ex.Message}");
        }
    }
}

/// <summary>
/// A small Oracle GraalVM zip in the shape of the real one: one top folder named after the build, the
/// binaries, the release file, and the folders that are left out when unpacking.
/// </summary>
internal static class JdkZip
{
    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <param name="tag">Written into the files, so two zips of the "same" build can be told apart on disk.</param>
    /// <param name="releaseText">The release file; by default one naming <paramref name="javaVersion"/>.</param>
    /// <param name="extra">Further entries; "{top}" in a name stands for <paramref name="top"/>.</param>
    public static byte[] Create(string top, string javaVersion, string tag = "", bool withJavaw = true, string? releaseText = null,
        params (string Name, string Content)[] extra)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, $"{top}/", "");
            Add(zip, $"{top}/bin/", "");
            Add(zip, $"{top}/bin/java.exe", $"java {tag}");
            if (withJavaw)
                Add(zip, $"{top}/bin/javaw.exe", $"javaw {tag}");
            Add(zip, $"{top}/release", releaseText ?? $"IMPLEMENTOR=\"Oracle Corporation\"\nJAVA_VERSION=\"{javaVersion}\"\nJAVA_VERSION_DATE=\"2026-07-21\"\n");
            Add(zip, $"{top}/lib/modules", $"modules {tag}");

            // What the unpacker must leave out.
            Add(zip, $"{top}/jmods/", "");
            Add(zip, $"{top}/jmods/a.jmod", "a");
            Add(zip, $"{top}/lib/static/b.lib", "b");
            Add(zip, $"{top}/lib/svm/c.jar", "c");
            Add(zip, $"{top}/lib/src.zip", "src");

            foreach (var (name, content) in extra)
                Add(zip, name.Replace("{top}", top), content);
        }

        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name);
        if (name.EndsWith('/'))
            return;

        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}

/// <summary>Answers requests from a table and counts them. Nothing here reaches the network.</summary>
internal sealed class FakeDownloadServer : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    public FakeDownloadServer Serve(string url, byte[] body) =>
        Route(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

    public FakeDownloadServer ServeText(string url, string text) =>
        Route(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });

    public FakeDownloadServer Fail(string url, HttpStatusCode status) =>
        Route(url, () => new HttpResponseMessage(status));

    public FakeDownloadServer Unreachable(string url) =>
        Route(url, () => throw new HttpRequestException("No such host is known."));

    public FakeDownloadServer Route(string url, Func<HttpResponseMessage> response)
    {
        lock (_gate)
        {
            _routes[url] = response;
        }

        return this;
    }

    public int Count(string url)
    {
        lock (_gate)
        {
            return _counts.GetValueOrDefault(url);
        }
    }

    public int TotalRequests
    {
        get
        {
            lock (_gate)
            {
                return _counts.Values.Sum();
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = request.RequestUri!.AbsoluteUri;
        Func<HttpResponseMessage>? route;
        lock (_gate)
        {
            _counts[url] = _counts.GetValueOrDefault(url) + 1;
            _routes.TryGetValue(url, out route);
        }

        if (route == null)
            throw new HttpRequestException($"The test has no route for {url}.");

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(route());
    }
}

/// <summary>Delivers some bytes, then nothing at all until the read is cancelled: a connection that went quiet.</summary>
internal sealed class StallingStream : Stream
{
    private readonly byte[] _firstChunk;
    private bool _delivered;

    public StallingStream(byte[] firstChunk) => _firstChunk = firstChunk;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_delivered)
        {
            _delivered = true;
            int count = Math.Min(_firstChunk.Length, buffer.Length);
            _firstChunk.AsMemory(0, count).CopyTo(buffer);
            return count;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
