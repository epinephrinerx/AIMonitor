using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;

namespace AIMonitor.Infrastructure.Linux;

[SupportedOSPlatform("linux")]
internal sealed class LibSecretKeyring : ISecretServiceKeyring
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] Libraries = ["libsecret-1.so.0", "libglib-2.0.so.0", "libgobject-2.0.so.0", "libgio-2.0.so.0"];
    private static readonly Lazy<IntPtr[]> Handles = new(LoadLibraries);
    private static readonly ConcurrentDictionary<string, IntPtr> Utf8 = new();

    public LibSecretKeyring() { }

    public SecretServiceResult Lookup(string schemaName, IReadOnlyDictionary<string, string> attributes)
    {
        Validate(schemaName, attributes);
        if (!Ready(out var unavailable)) return unavailable;
        return Invoke<IntPtr>(schemaName, attributes, (IntPtr schema, IntPtr table, IntPtr cancellable, List<IntPtr> allocations, out IntPtr error) => SecretPasswordLookupvSync(schema, table, cancellable, out error), LookupResult);
    }

    public SecretServiceResult Store(string schemaName, IReadOnlyDictionary<string, string> attributes, string label, string secret)
    {
        Validate(schemaName, attributes);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (label.Contains('\0')) throw new ArgumentException("Value cannot contain NUL.", nameof(label));
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length == 0 || secret.Contains('\0')) throw new ArgumentException("Value is empty or contains NUL.", nameof(secret));
        if (!Ready(out var unavailable)) return unavailable;
        IntPtr secretPointer = IntPtr.Zero;
        return Invoke<int>(schemaName, attributes, (IntPtr schema, IntPtr table, IntPtr cancellable, List<IntPtr> allocations, out IntPtr error) =>
        {
            secretPointer = Alloc(secret);
            try
            {
                return SecretPasswordStorevSync(schema, table, IntPtr.Zero, AllocTracked(label, allocations), secretPointer, cancellable, out error);
            }
            finally
            {
                ZeroUtf8(secretPointer, secret);
                Marshal.FreeCoTaskMem(secretPointer);
                secretPointer = IntPtr.Zero;
            }
        },
            value => value != IntPtr.Zero ? new SecretServiceResult(SecretServiceStatus.Done) : new SecretServiceResult(SecretServiceStatus.Failed, Message: SecretServiceErrors.StoreFalseMessage));
    }

    public SecretServiceResult Clear(string schemaName, IReadOnlyDictionary<string, string> attributes)
    {
        Validate(schemaName, attributes);
        if (!Ready(out var unavailable)) return unavailable;
        return Invoke<int>(schemaName, attributes, (IntPtr schema, IntPtr table, IntPtr cancellable, List<IntPtr> allocations, out IntPtr error) => SecretPasswordClearvSync(schema, table, cancellable, out error),
            value => value != IntPtr.Zero ? new SecretServiceResult(SecretServiceStatus.Done) : new SecretServiceResult(SecretServiceStatus.NotFound));
    }

    private delegate T NativeCall<T>(IntPtr schema, IntPtr table, IntPtr cancellable, List<IntPtr> allocations, out IntPtr error);
    private delegate SecretServiceResult ConvertResult<T>(T value);

    private static SecretServiceResult Invoke<T>(string name, IReadOnlyDictionary<string, string> attributes, NativeCall<T> call, ConvertResult<T> convert)
    {
        var handles = Handles.Value;
        var hash = GHashTableNew(Export(handles[1], "g_str_hash"), Export(handles[1], "g_str_equal"));
        var schemaAttributes = GHashTableNew(Export(handles[1], "g_str_hash"), Export(handles[1], "g_str_equal"));
        IntPtr schema = IntPtr.Zero, cancellable = IntPtr.Zero, error = IntPtr.Zero;
        var allocations = new List<IntPtr>();
        try
        {
            foreach (var item in attributes)
            {
                var key = Alloc(item.Key); allocations.Add(key);
                var value = Alloc(item.Value); allocations.Add(value);
                GHashTableInsert(hash, key, value);
                GHashTableInsert(schemaAttributes, AllocTracked(item.Key, allocations), IntPtr.Zero);
            }
            schema = SecretSchemaNewv(AllocTracked(name, allocations), 0, schemaAttributes);
            cancellable = GCancellableNew();
            var timedOut = 0;
            using var timer = new Timer(_ => { Volatile.Write(ref timedOut, 1); GCancellableCancel(cancellable); }, null, CallTimeout, Timeout.InfiniteTimeSpan);
            var valueResult = call(schema, hash, cancellable, allocations, out error);
            using (var callbackFinished = new ManualResetEvent(false))
            {
                if (timer.Dispose(callbackFinished)) callbackFinished.WaitOne();
            }
            if (error != IntPtr.Zero)
            {
                var domain = GQuarkToString(unchecked((uint)Marshal.ReadInt32(error)));
                var domainName = Marshal.PtrToStringUTF8(domain);
                var code = Marshal.ReadInt32(error, 4);
                var status = SecretServiceErrors.Classify(domainName, code, Volatile.Read(ref timedOut) != 0);
                var safeDomain = domainName is "g-io-error-quark" or "g-dbus-error-quark" or "secret-error" ? domainName : "other";
                GErrorFree(error); error = IntPtr.Zero;
                if (typeof(T) == typeof(IntPtr) && valueResult is IntPtr secret && secret != IntPtr.Zero) SecretPasswordFree(secret);
                return new SecretServiceResult(status, Message: Volatile.Read(ref timedOut) != 0 ? SecretServiceErrors.TimeoutMessage : $"{safeDomain} {code}");
            }
            if (Volatile.Read(ref timedOut) != 0 && EqualityComparer<T>.Default.Equals(valueResult, default!))
                return new SecretServiceResult(SecretServiceStatus.Unavailable, Message: SecretServiceErrors.TimeoutMessage);
            return convert(valueResult);
        }
        catch (DllNotFoundException) { return new SecretServiceResult(SecretServiceStatus.Unavailable, Message: SecretServiceErrors.LibraryMissingMessage); }
        catch (EntryPointNotFoundException) { return new SecretServiceResult(SecretServiceStatus.Unavailable, Message: SecretServiceErrors.LibraryMissingMessage); }
        finally
        {
            if (error != IntPtr.Zero) GErrorFree(error);
            if (cancellable != IntPtr.Zero) GObjectUnref(cancellable);
            if (schema != IntPtr.Zero) SecretSchemaUnref(schema);
            if (hash != IntPtr.Zero) GHashTableUnref(hash);
            if (schemaAttributes != IntPtr.Zero) GHashTableUnref(schemaAttributes);
            foreach (var ptr in allocations)
            {
                if (ptr == IntPtr.Zero) continue;
                Marshal.FreeCoTaskMem(ptr);
            }
        }
    }

    private static SecretServiceResult LookupResult(IntPtr value)
    {
        if (value == IntPtr.Zero) return new SecretServiceResult(SecretServiceStatus.NotFound);
        try { return new SecretServiceResult(SecretServiceStatus.Found, Marshal.PtrToStringUTF8(value)); }
        finally { SecretPasswordFree(value); }
    }
    private static bool Ready(out SecretServiceResult result)
    {
        if (!SecretServiceErrors.HasSessionBus(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"), Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), File.Exists))
        { result = new(SecretServiceStatus.Unavailable, Message: SecretServiceErrors.NoSessionBusMessage); return false; }
        try { _ = Handles.Value; result = default; return true; }
        catch (DllNotFoundException) { result = new(SecretServiceStatus.Unavailable, Message: SecretServiceErrors.LibraryMissingMessage); return false; }
    }
    private static IntPtr[] LoadLibraries()
    {
        var result = new IntPtr[Libraries.Length];
        for (var i = 0; i < Libraries.Length; i++) if (!NativeLibrary.TryLoad(Libraries[i], out result[i])) throw new DllNotFoundException();
        return result;
    }
    private static IntPtr Export(IntPtr handle, string name) => NativeLibrary.GetExport(handle, name);
    private static IntPtr Alloc(string value) => Marshal.StringToCoTaskMemUTF8(value);
    private static void ZeroUtf8(IntPtr pointer, string value)
    {
        var length = System.Text.Encoding.UTF8.GetByteCount(value);
        for (var index = 0; index < length; index++) Marshal.WriteByte(pointer, index, 0);
    }
    private static IntPtr AllocTracked(string value, List<IntPtr> allocations) { var p = Alloc(value); allocations.Add(p); return p; }
    private static void Validate(string name, IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0')) throw new ArgumentException("Invalid schema name.", nameof(name));
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.Count is 0 or > 32) throw new ArgumentException("Invalid attribute count.", nameof(attributes));
        foreach (var pair in attributes)
        {
            ArgumentNullException.ThrowIfNull(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            if (pair.Key.Length == 0 || pair.Key.Contains('\0') || pair.Value.Contains('\0')) throw new ArgumentException("Invalid attribute.", nameof(attributes));
        }
    }

    [DllImport("libglib-2.0.so.0", EntryPoint = "g_hash_table_new")] private static extern IntPtr GHashTableNew(IntPtr hash, IntPtr equal);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_hash_table_insert")] private static extern int GHashTableInsert(IntPtr table, IntPtr key, IntPtr value);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_hash_table_unref")] private static extern void GHashTableUnref(IntPtr table);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_error_free")] private static extern void GErrorFree(IntPtr error);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_quark_to_string")] private static extern IntPtr GQuarkToString(uint quark);
    [DllImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_new")] private static extern IntPtr GCancellableNew();
    [DllImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_cancel")] private static extern void GCancellableCancel(IntPtr cancellable);
    [DllImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")] private static extern void GObjectUnref(IntPtr obj);
    [DllImport("libsecret-1.so.0", EntryPoint = "secret_schema_newv")] private static extern IntPtr SecretSchemaNewv(IntPtr name, int flags, IntPtr attributes);
    [DllImport("libsecret-1.so.0", EntryPoint = "secret_schema_unref")] private static extern void SecretSchemaUnref(IntPtr schema);
    [DllImport("libsecret-1.so.0", EntryPoint = "secret_password_lookupv_sync")] private static extern IntPtr SecretPasswordLookupvSync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport("libsecret-1.so.0", EntryPoint = "secret_password_storev_sync")] private static extern int SecretPasswordStorevSync(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error);
    [DllImport("libsecret-1.so.0", EntryPoint = "secret_password_clearv_sync")] private static extern int SecretPasswordClearvSync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport("libsecret-1.so.0", EntryPoint = "secret_password_free")] private static extern void SecretPasswordFree(IntPtr password);
}
