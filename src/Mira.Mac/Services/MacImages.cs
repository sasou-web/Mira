using System.Runtime.InteropServices;

namespace Mira.Mac.Services;

/// <summary>
/// Pictures read by macOS itself (ImageIO), for what the Skia of the Mac does not read: HEIC first, an iPhone's photos
/// (that Skia is built without HEIF), and TIFF or a camera's RAW. One comes back as a PNG turned as the camera was held
/// (HEIF's « irot », EXIF's orientation), its shorter side brought down to what the caller needs, never up. Null off
/// macOS, or when macOS cannot read the picture either.
/// </summary>
internal static partial class MacImages
{
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
    private const nint SInt64 = 4; // kCFNumberSInt64Type (a CFNumberType is a CFIndex)

    // CGImageSource.h, CGImageDestination.h, CFData.h, CFDictionary.h, CFNumber.h and CFBase.h: every …Ref a pointer,
    // size_t unsigned, CFIndex (and CFNumberType) a signed long, CFTypeID an unsigned long, bool and Boolean one byte.
    [LibraryImport(ImageIO)] private static partial IntPtr CGImageSourceCreateWithData(IntPtr data, IntPtr options);
    [LibraryImport(ImageIO)] private static partial IntPtr CGImageSourceCopyPropertiesAtIndex(IntPtr source, nuint index, IntPtr options);
    [LibraryImport(ImageIO)] private static partial IntPtr CGImageSourceCreateThumbnailAtIndex(IntPtr source, nuint index, IntPtr options);
    [LibraryImport(ImageIO)] private static partial IntPtr CGImageDestinationCreateWithData(IntPtr data, IntPtr type, nuint count, IntPtr options);
    [LibraryImport(ImageIO)] private static partial void CGImageDestinationAddImage(IntPtr destination, IntPtr image, IntPtr properties);
    [LibraryImport(ImageIO)][return: MarshalAs(UnmanagedType.U1)] private static partial bool CGImageDestinationFinalize(IntPtr destination);
    [LibraryImport(CoreFoundation)] private static partial IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [LibraryImport(CoreFoundation)] private static partial IntPtr CFDataCreateMutable(IntPtr allocator, nint capacity);
    [LibraryImport(CoreFoundation)] private static partial IntPtr CFDataGetBytePtr(IntPtr data);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataGetLength(IntPtr data);
    [LibraryImport(CoreFoundation)] private static partial IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);
    [LibraryImport(CoreFoundation)] private static partial IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
    [LibraryImport(CoreFoundation)] private static partial IntPtr CFNumberCreate(IntPtr allocator, nint type, ref long value);
    [LibraryImport(CoreFoundation)][return: MarshalAs(UnmanagedType.U1)] private static partial bool CFNumberGetValue(IntPtr number, nint type, out long value);
    [LibraryImport(CoreFoundation)] private static partial nuint CFGetTypeID(IntPtr value);
    [LibraryImport(CoreFoundation)] private static partial nuint CFNumberGetTypeID();
    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(IntPtr value);

    private sealed record Names(IntPtr FromImageAlways, IntPtr WithTransform, IntPtr MaxPixelSize, IntPtr PixelWidth, IntPtr PixelHeight,
        IntPtr True, IntPtr KeyCallBacks, IntPtr ValueCallBacks);
    // The options' keys are the CFStrings the frameworks export, kCFBooleanTrue, and (by address) the standard callbacks.
    private static readonly Lazy<Names?> Constants = new(() =>
    {
        try
        {
            IntPtr imageIO = NativeLibrary.Load(ImageIO), cf = NativeLibrary.Load(CoreFoundation);
            IntPtr Value(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
            return new Names(Value(imageIO, "kCGImageSourceCreateThumbnailFromImageAlways"), Value(imageIO, "kCGImageSourceCreateThumbnailWithTransform"),
                Value(imageIO, "kCGImageSourceThumbnailMaxPixelSize"), Value(imageIO, "kCGImagePropertyPixelWidth"), Value(imageIO, "kCGImagePropertyPixelHeight"),
                Value(cf, "kCFBooleanTrue"), NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    });

    /// <summary>The picture as a PNG the right way up, its shorter side <paramref name="shortSide"/> px when it had more.</summary>
    public static byte[]? UprightPng(byte[] picture, int shortSide)
    {
        if (!OperatingSystem.IsMacOS() || picture.Length == 0) return null;
        try { return Constants.Value is { } names ? Convert(names, picture, shortSide) : null; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private static byte[]? Convert(Names names, byte[] picture, int shortSide)
    {
        var made = new List<IntPtr>();
        IntPtr Keep(IntPtr value) { if (value != IntPtr.Zero) made.Add(value); return value; }
        try
        {
            var data = Keep(CFDataCreate(IntPtr.Zero, picture, picture.Length));
            var source = data == IntPtr.Zero ? IntPtr.Zero : Keep(CGImageSourceCreateWithData(data, IntPtr.Zero));
            if (source == IntPtr.Zero) return null;
            // A thumbnail made from the picture itself (not the small one a file may carry), turned by its orientation.
            var options = new List<(IntPtr Key, IntPtr Value)> { (names.FromImageAlways, names.True), (names.WithTransform, names.True) };
            // Its size as kept (turning swaps the sides, not their ratio): its longer side for a shorter one of shortSide.
            var properties = Keep(CGImageSourceCopyPropertiesAtIndex(source, 0, IntPtr.Zero));
            long width = Number(properties, names.PixelWidth), height = Number(properties, names.PixelHeight);
            if (width > 0 && height > 0)
            {
                long longest = Math.Max(width, height), wanted = (long)Math.Ceiling((double)shortSide * longest / Math.Min(width, height));
                if (wanted < longest && Keep(CFNumberCreate(IntPtr.Zero, SInt64, ref wanted)) is var size && size != IntPtr.Zero) options.Add((names.MaxPixelSize, size));
            }
            var dictionary = Keep(CFDictionaryCreate(IntPtr.Zero, [.. options.Select(x => x.Key)], [.. options.Select(x => x.Value)], options.Count, names.KeyCallBacks, names.ValueCallBacks));
            var image = dictionary == IntPtr.Zero ? IntPtr.Zero : Keep(CGImageSourceCreateThumbnailAtIndex(source, 0, dictionary));
            if (image == IntPtr.Zero) return null;
            IntPtr png = Keep(CFDataCreateMutable(IntPtr.Zero, 0)), type = Keep(CFStringCreateWithCString(IntPtr.Zero, "public.png", Utf8));
            var destination = png == IntPtr.Zero || type == IntPtr.Zero ? IntPtr.Zero : Keep(CGImageDestinationCreateWithData(png, type, 1, IntPtr.Zero));
            if (destination == IntPtr.Zero) return null;
            CGImageDestinationAddImage(destination, image, IntPtr.Zero);
            return CGImageDestinationFinalize(destination) ? Bytes(png) : null;
        }
        // Each made by a Create or Copy call, so this code's to release; CFRelease(NULL) crashes.
        finally { foreach (var value in made) CFRelease(value); }
    }

    private static long Number(IntPtr dictionary, IntPtr key)
    {
        var value = dictionary == IntPtr.Zero ? IntPtr.Zero : CFDictionaryGetValue(dictionary, key);
        return value != IntPtr.Zero && CFGetTypeID(value) == CFNumberGetTypeID() && CFNumberGetValue(value, SInt64, out var number) ? number : 0;
    }
    private static byte[] Bytes(IntPtr data)
    {
        var bytes = new byte[checked((int)CFDataGetLength(data))];
        if (bytes.Length > 0) Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
        return bytes;
    }
}
