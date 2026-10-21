#if NETFRAMEWORK
// Civil 3D 2024 runs the plugin on .NET Framework 4.8. This file is compiled only for that build and
// supplies the .NET 8+ members the shared source calls, so the 2026/2027 code paths stay untouched.
using System.ComponentModel;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Civil3DMcpPlugin;

internal static class NetFrameworkPolyfills
{
  private const StringSplitOptions TrimEntriesFlag = (StringSplitOptions)2;
  private const int MoveFileReplaceExisting = 0x1;
  private const int MoveFileCopyAllowed = 0x2;

  // acad.exe never applies a plugin's binding redirects, and AutoCAD 2024 ships an older
  // System.Runtime.CompilerServices.Unsafe than System.Text.Json needs. Resolve anything bundled
  // next to this assembly by simple name; the module initializer runs before any JSON type loads.
  [ModuleInitializer]
  internal static void RegisterBundledAssemblyResolver()
  {
    var folder = Path.GetDirectoryName(typeof(NetFrameworkPolyfills).Assembly.Location) ?? "";
    if (folder.Length == 0)
    {
      return;
    }

    AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
    {
      var candidate = Path.Combine(folder, new AssemblyName(args.Name).Name + ".dll");
      return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
    };
  }

  extension(Math)
  {
    public static int Clamp(int value, int min, int max)
    {
      if (min > max) throw new ArgumentException($"'{min}' cannot be greater than {max}.");
      return value < min ? min : value > max ? max : value;
    }

    public static double Clamp(double value, double min, double max)
    {
      if (min > max) throw new ArgumentException($"'{min}' cannot be greater than {max}.");
      return value < min ? min : value > max ? max : value;
    }
  }

  extension(double)
  {
    public static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
  }

  extension(Path)
  {
    // Same rules as .NET's Windows implementation: drive-rooted (C:\) or UNC/device (\\, \\?\).
    public static bool IsPathFullyQualified(string path)
    {
      if (path == null) throw new ArgumentNullException(nameof(path));
      if (path.Length < 2) return false;
      if (IsSeparator(path[0])) return path[1] == '?' || IsSeparator(path[1]);
      var drive = (char)(path[0] | 0x20);
      return path.Length >= 3 && path[1] == ':' && IsSeparator(path[2]) && drive >= 'a' && drive <= 'z';
    }

    // .NET semantics FileBoundary relies on: "." for the same folder, ".." segments to climb,
    // the full target path when the roots differ; case-insensitive like Windows.
    // shortcut: drops a trailing separator .NET would keep; callers only test prefixes/segments.
    public static string GetRelativePath(string relativeTo, string path)
    {
      var from = Path.GetFullPath(relativeTo);
      var to = Path.GetFullPath(path);
      if (!string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
      {
        return to;
      }

      var fromParts = from.TrimEnd('\\').Split('\\');
      var toParts = to.TrimEnd('\\').Split('\\');
      var common = 0;
      while (common < fromParts.Length && common < toParts.Length
        && string.Equals(fromParts[common], toParts[common], StringComparison.OrdinalIgnoreCase))
      {
        common++;
      }

      var relative = string.Join("\\", Enumerable.Repeat("..", fromParts.Length - common).Concat(toParts.Skip(common)));
      return relative.Length == 0 ? "." : relative;
    }
  }

  extension(File)
  {
    public static Task<string> ReadAllTextAsync(string path, Encoding encoding) =>
      Task.Run(() => File.ReadAllText(path, encoding));

    // Overwrite uses the same Win32 call as .NET, so it stays an atomic replace.
    public static void Move(string sourceFileName, string destFileName, bool overwrite)
    {
      if (!overwrite)
      {
        File.Move(sourceFileName, destFileName);
        return;
      }

      if (!MoveFileEx(Path.GetFullPath(sourceFileName), Path.GetFullPath(destFileName), MoveFileCopyAllowed | MoveFileReplaceExisting))
      {
        throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
      }
    }
  }

  extension(Convert)
  {
    public static string ToHexString(byte[] inArray, int offset, int length) =>
      BitConverter.ToString(inArray, offset, length).Replace("-", "");
  }

  extension(SHA256)
  {
    public static byte[] HashData(byte[] source)
    {
      using var sha = SHA256.Create();
      return sha.ComputeHash(source);
    }
  }

  extension(Encoding)
  {
    public static Encoding Latin1 => Encoding.GetEncoding(28591);
  }

  extension(Enum)
  {
    public static string[] GetNames<TEnum>() where TEnum : struct, Enum => Enum.GetNames(typeof(TEnum));
  }

  extension(StringSplitOptions)
  {
    public static StringSplitOptions TrimEntries => TrimEntriesFlag;
  }

  public static string[] Split(this string text, char separator, StringSplitOptions options)
  {
    IEnumerable<string> parts = text.Split(separator);
    if ((options & TrimEntriesFlag) != 0) parts = parts.Select(part => part.Trim());
    if ((options & StringSplitOptions.RemoveEmptyEntries) != 0) parts = parts.Where(part => part.Length > 0);
    return parts.ToArray();
  }

  public static bool StartsWith(this string text, char value) => text.Length > 0 && text[0] == value;

  public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key) =>
    dictionary.TryGetValue(key, out var value) ? value : default;

  public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
  {
    key = pair.Key;
    value = pair.Value;
  }

  public static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> source) => source.OrderBy(item => item);

  // RpcTcpServer.Stop() closes the listener, which faults the pending accept; the loop then sees the token.
  public static Task<TcpClient> AcceptTcpClientAsync(this TcpListener listener, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return listener.AcceptTcpClientAsync();
  }

  // Also takes byte[] through its implicit conversion to ReadOnlyMemory<byte>.
  public static Task WriteAsync(this Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
    MemoryMarshal.TryGetArray(buffer, out var segment)
      ? stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken)
      : stream.WriteAsync(buffer.ToArray(), 0, buffer.Length, cancellationToken);

  // .NET Framework's NetworkStream ignores the token once a read is pending, which would leave the
  // disconnect monitor (and so every RPC response) waiting forever. Abandon the read on cancellation;
  // callers dispose the stream right after, which ends the abandoned read.
  public static async Task<int> ReadAsync(this Stream stream, byte[] buffer, CancellationToken cancellationToken)
  {
    var read = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
    if (!cancellationToken.CanBeCanceled || read.IsCompleted)
    {
      return await read.ConfigureAwait(false);
    }

    var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
    {
      if (await Task.WhenAny(read, cancelled.Task).ConfigureAwait(false) != read)
      {
        _ = read.ContinueWith(task => task.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        throw new OperationCanceledException(cancellationToken);
      }
    }

    return await read.ConfigureAwait(false);
  }

  private static bool IsSeparator(char c) => c == '\\' || c == '/';

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
}
#endif
