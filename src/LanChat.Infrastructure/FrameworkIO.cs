using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace LanChat.Infrastructure;

public static class FrameworkIO
{
    public static TimeSpan Elapsed(long start) => TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency);
    public static byte[] RandomBytes(int count) { using var rng = RandomNumberGenerator.Create(); var data = new byte[count]; rng.GetBytes(data); return data; }
    public static byte[] Hash(byte[] data) { using var sha = SHA256.Create(); return sha.ComputeHash(data); }
    public static byte[] Hmac(byte[] key, byte[] data) { using var hmac = new HMACSHA256(key); return hmac.ComputeHash(data); }
    public static bool FixedEquals(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        var diff = 0;
        for (var j = 0; j < left.Length; j++) diff |= left[j] ^ right[j];
        return diff == 0;
    }

    public static async Task<byte[]> HashAsync(Stream stream, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) != 0)
            hash.AppendData(buffer, 0, count);
        return hash.GetHashAndReset();
    }

    public static async Task<byte[]> CopyAndHashAsync(Stream source, Stream destination, CancellationToken ct)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; int count;
        while ((count = await source.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
        {
            digest.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer, 0, count, ct).ConfigureAwait(false);
        }
        return digest.GetHashAndReset();
    }

    public static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");
    public static Task<string> ReadTextAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => File.ReadAllText(path), ct);
    public static Task<byte[]> ReadBytesAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => File.ReadAllBytes(path), ct);
    public static Task WriteTextAsync(string path, string text, CancellationToken ct = default) =>
        Task.Run(() => File.WriteAllText(path, text), ct);
    public static Task WriteBytesAsync(string path, byte[] data, CancellationToken ct = default) =>
        Task.Run(() => File.WriteAllBytes(path, data), ct);

    public static void PublishFile(string temp, string path)
    {
        if (File.Exists(path))
        {
            try { File.Replace(temp, path, null); return; }
            catch (FileNotFoundException) when (File.Exists(temp) && !File.Exists(path)) { }
        }
        try { File.Move(temp, path); }
        catch (IOException) when (File.Exists(temp) && File.Exists(path)) { File.Replace(temp, path, null); }
    }

    public static Task CopyToAsync(this Stream stream, Stream destination, CancellationToken ct) =>
        stream.CopyToAsync(destination, 65536, ct);
    public static Task WriteAsync(this Stream stream, byte[] bytes, CancellationToken ct) =>
        stream.WriteAsync(bytes, 0, bytes.Length, ct);

    public static async Task ReadExactlyAsync(this Stream stream, byte[] bytes, CancellationToken ct)
    {
        for (var offset = 0; offset < bytes.Length;)
        {
            var count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, ct).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
    }

    public static async Task ConnectAsync(this TcpClient client, IPAddress address, int port, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var abort = ct.Register(() => client.Close());
        try { await client.ConnectAsync(address, port).ConfigureAwait(false); }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        ct.ThrowIfCancellationRequested();
    }

    public static async Task ForEachAsync<T>(IEnumerable<T> items, int concurrency, CancellationToken ct, Func<T, CancellationToken, Task> action)
    {
        using var iterator = items.GetEnumerator();
        var gate = new object();
        async Task Worker()
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                T item;
                lock (gate)
                {
                    if (!iterator.MoveNext()) return;
                    item = iterator.Current;
                }
                await action(item, ct).ConfigureAwait(false);
            }
        }
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Task.Run(Worker, ct))).ConfigureAwait(false);
    }

    public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key, TValue fallback = default!) =>
        dictionary.TryGetValue(key, out var value) ? value : fallback;
}
