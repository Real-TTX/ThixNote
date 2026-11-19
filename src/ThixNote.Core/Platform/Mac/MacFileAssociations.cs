using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ThixNote.Core.Platform.Mac;

/// <summary>
/// macOS (SETUP-08): the bundle declares its file types in Info.plist (CFBundleDocumentTypes, scripts/package-macos.sh), so it is
/// offered in "Öffnen mit"; <see cref="MakeDefault"/> makes it the app that opens them (Launch Services, per user).
/// </summary>
[SupportedOSPlatform("macos")]
public static partial class MacFileAssociations
{
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;
    private const uint AllRoles = 0xFFFFFFFF;

    /// <summary>The app <paramref name="bundleId"/> opens files with these extensions (".md").</summary>
    public static void MakeDefault(string bundleId, IEnumerable<string> extensions)
    {
        var handler = CFStringCreateWithCString(IntPtr.Zero, bundleId, Utf8);
        var tagClass = CFStringCreateWithCString(IntPtr.Zero, "public.filename-extension", Utf8);
        try
        {
            foreach (var extension in extensions)
            {
                var tag = CFStringCreateWithCString(IntPtr.Zero, extension.TrimStart('.'), Utf8);
                var type = UTTypeCreatePreferredIdentifierForTag(tagClass, tag, IntPtr.Zero);
                if (type != IntPtr.Zero)
                {
                    var status = LSSetDefaultRoleHandlerForContentType(type, AllRoles, handler);
                    if (status != 0)
                    {
                        Infrastructure.Log.Warning($"Launch Services did not take {bundleId} for {extension} ({status})");
                    }

                    CFRelease(type);
                }

                CFRelease(tag);
            }
        }
        finally
        {
            CFRelease(tagClass);
            CFRelease(handler);
        }
    }

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr value);

    [LibraryImport(CoreServices)]
    private static partial IntPtr UTTypeCreatePreferredIdentifierForTag(IntPtr tagClass, IntPtr tag, IntPtr conformingTo);

    [LibraryImport(CoreServices)]
    private static partial int LSSetDefaultRoleHandlerForContentType(IntPtr contentType, uint role, IntPtr handlerBundleId);
}
