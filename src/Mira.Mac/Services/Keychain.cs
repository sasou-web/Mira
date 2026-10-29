using System.Runtime.InteropServices;
using System.Text;

namespace Mira.Mac.Services;

/// <summary>
/// The login keychain, through Security.framework's SecItem calls (SecKeychain…GenericPassword is deprecated since
/// macOS 10.10): one generic password « Mira » per profile, the profile's folder as its account, and « Mira (Tailscale) »
/// for the key of its Tailscale node, the node's folder as its account. The item trusts the copy
/// of Mira that wrote it: macOS asks before another program, or a later Mira (signed ad hoc, so another identity for
/// macOS), reads it. Not the data protection keychain: it needs an entitlement only an Apple developer account signs.
/// Every failure, a missing framework included, answers false or null; <see cref="Status"/> keeps the code.
/// </summary>
public sealed partial class Keychain : ITokenStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
    private const string Service = "Mira";
    // OSStatus codes from SecBase.h.
    public const int Success = 0, ItemNotFound = -25300, InteractionNotAllowed = -25308, NoSuchKeychain = -25294,
        NotAvailable = -25291, AuthFailed = -25293, UserCanceled = -128, Allocate = -108, Unimplemented = -4;

    /// <summary>The last code Security.framework answered (SecBase.h); <see cref="Unimplemented"/> when it could not be called.</summary>
    public int Status { get; private set; }

    // Declarations from SecItem.h, CFDictionary.h, CFString.h, CFData.h and CFBase.h: OSStatus is SInt32, CFIndex a signed
    // long and CFTypeID an unsigned long (64 bits on Apple Silicon), every CF…Ref a pointer.
    [LibraryImport(Security)] private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [LibraryImport(Security)] private static partial int SecItemAdd(IntPtr attributes, IntPtr result);
    [LibraryImport(Security)] private static partial int SecItemDelete(IntPtr query);
    [LibraryImport(CoreFoundation)] private static partial IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);
    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);
    [LibraryImport(CoreFoundation)] private static partial IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [LibraryImport(CoreFoundation)] private static partial IntPtr CFDataGetBytePtr(IntPtr data);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataGetLength(IntPtr data);
    [LibraryImport(CoreFoundation)] private static partial nuint CFGetTypeID(IntPtr value);
    [LibraryImport(CoreFoundation)] private static partial nuint CFDataGetTypeID();
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(IntPtr value);

    private sealed record Names(IntPtr Class, IntPtr GenericPassword, IntPtr Service, IntPtr Account, IntPtr Label, IntPtr Data,
        IntPtr ReturnData, IntPtr MatchLimit, IntPtr MatchOne, IntPtr True, IntPtr KeyCallBacks, IntPtr ValueCallBacks);
    // The dictionaries' keys and values are constants the frameworks export: the CFStrings their variables point to,
    // kCFBooleanTrue, and (by address) the standard callbacks that retain each key and value.
    private static readonly Lazy<Names?> Constants = new(() =>
    {
        try
        {
            IntPtr security = NativeLibrary.Load(Security), cf = NativeLibrary.Load(CoreFoundation);
            IntPtr Value(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
            return new Names(Value(security, "kSecClass"), Value(security, "kSecClassGenericPassword"), Value(security, "kSecAttrService"),
                Value(security, "kSecAttrAccount"), Value(security, "kSecAttrLabel"), Value(security, "kSecValueData"), Value(security, "kSecReturnData"),
                Value(security, "kSecMatchLimit"), Value(security, "kSecMatchLimitOne"), Value(cf, "kCFBooleanTrue"),
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    });

    public bool Store(string account, string token, string label = Service) => Item(account, false, (names, item) =>
    {
        // A new item each time rather than an update: it trusts this copy of Mira, which then reads it without a question.
        if (!Remove(names, item)) return false;
        var bytes = Encoding.UTF8.GetBytes(token);
        IntPtr data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length), name = Text(label);
        try { return data != IntPtr.Zero && name != IntPtr.Zero && With(names, [.. item, (names.Label, name), (names.Data, data)], attributes => SecItemAdd(attributes, IntPtr.Zero)) == Success; }
        finally { Release(data); Release(name); }
    });
    /// <summary>The last <see cref="Read"/> found no item for its account: not one it could not read.</summary>
    public bool Missing => Status == ItemNotFound;

    public string? Read(string account) => Item(account, (string?)null, (names, item) =>
    {
        var result = IntPtr.Zero;
        try
        {
            if (With(names, [.. item, (names.ReturnData, names.True), (names.MatchLimit, names.MatchOne)], query => SecItemCopyMatching(query, out result)) != Success) return null;
            return result != IntPtr.Zero && CFGetTypeID(result) == CFDataGetTypeID() ? Encoding.UTF8.GetString(Bytes(result)) : null;
        }
        finally { Release(result); }
    });

    public bool Delete(string account) => Item(account, false, Remove);

    private bool Remove(Names names, (IntPtr Key, IntPtr Value)[] item) => With(names, item, SecItemDelete) is Success or ItemNotFound;

    /// <summary>Calls <paramref name="call"/> with what names the item: its class, service and account.</summary>
    private T Item<T>(string account, T failed, Func<Names, (IntPtr Key, IntPtr Value)[], T> call)
    {
        try
        {
            if (Constants.Value is not { } names) { Status = Unimplemented; return failed; }
            IntPtr service = Text(Service), user = Text(account);
            try
            {
                if (service == IntPtr.Zero || user == IntPtr.Zero) { Status = Allocate; return failed; }
                return call(names, [(names.Class, names.GenericPassword), (names.Service, service), (names.Account, user)]);
            }
            finally { Release(service); Release(user); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { Status = Unimplemented; return failed; }
    }
    /// <summary>A CFDictionary of <paramref name="entries"/> for one SecItem call, released after it.</summary>
    private int With(Names names, (IntPtr Key, IntPtr Value)[] entries, Func<IntPtr, int> call)
    {
        var dictionary = CFDictionaryCreate(IntPtr.Zero, [.. entries.Select(x => x.Key)], [.. entries.Select(x => x.Value)], entries.Length, names.KeyCallBacks, names.ValueCallBacks);
        if (dictionary == IntPtr.Zero) return Status = Allocate;
        try { return Status = call(dictionary); }
        finally { CFRelease(dictionary); }
    }
    private static IntPtr Text(string text) => CFStringCreateWithCString(IntPtr.Zero, text, Utf8);
    private static byte[] Bytes(IntPtr data)
    {
        var bytes = new byte[checked((int)CFDataGetLength(data))];
        if (bytes.Length > 0) Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
        return bytes;
    }
    // CFRelease(NULL) crashes.
    private static void Release(IntPtr value) { if (value != IntPtr.Zero) CFRelease(value); }
}
