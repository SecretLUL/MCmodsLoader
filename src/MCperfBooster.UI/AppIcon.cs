using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using MCperfBooster.UI.Interop;

namespace MCperfBooster.UI;

/// <summary>Loads the shared branding from the binary, including in NativeAOT builds.</summary>
internal static class AppIcon
{
    public static nint Load(int size)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MCperfBooster.AppIcon")
            ?? throw new InvalidOperationException("The application icon is missing.");
        using var reader = new BinaryReader(stream);
        reader.ReadUInt16(); // Reserved.
        if (reader.ReadUInt16() != 1) throw new InvalidDataException("Invalid application icon.");
        int count = reader.ReadUInt16();
        int bestSize = 0;
        uint bestLength = 0, bestOffset = 0;
        for (int i = 0; i < count; i++)
        {
            int width = reader.ReadByte();
            if (width == 0) width = 256;
            reader.ReadBytes(7); // Height, palette, reserved, planes and bit depth.
            uint length = reader.ReadUInt32(), offset = reader.ReadUInt32();
            // Prefer the smallest frame at least as large as requested; otherwise the largest.
            if (bestSize == 0 || (bestSize < size && width > bestSize) ||
                (width >= size && width < bestSize))
            {
                bestSize = width;
                bestLength = length;
                bestOffset = offset;
            }
        }
        stream.Position = bestOffset;
        byte[] frame = reader.ReadBytes(checked((int)bestLength));
        nint icon = Native.CreateIconFromResourceEx(frame, (uint)frame.Length, true, 0x00030000, size, size, 0);
        if (icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return icon;
    }
}
