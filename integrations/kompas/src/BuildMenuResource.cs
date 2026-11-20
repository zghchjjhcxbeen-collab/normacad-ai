// Build a standard Win32 .res without requiring rc.exe / Windows SDK.
// SDK CSharp.zip Automation/Step2.RES uses RT_MENU=4, ID=2000,
// a version-0 MENU template and a flat list of command IDs.
using System;
using System.IO;
using System.Text;

internal static class BuildMenuResource
{
    private static void Header(BinaryWriter writer, int size, ushort type, ushort name)
    {
        writer.Write(size);
        writer.Write(32);
        writer.Write((ushort)0xffff); writer.Write(type);
        writer.Write((ushort)0xffff); writer.Write(name);
        writer.Write(0); // DataVersion
        writer.Write((ushort)0x1030); // MOVEABLE | PURE | DISCARDABLE
        writer.Write((ushort)0x419); // Russian
        writer.Write(0); writer.Write(0); // Version, Characteristics
    }

    private static void Item(BinaryWriter writer, ushort flags, ushort command, string text)
    {
        writer.Write(flags);
        writer.Write(command);
        writer.Write(Encoding.Unicode.GetBytes(text + "\0"));
    }

    public static void Main(string[] args)
    {
        using (MemoryStream data = new MemoryStream())
        using (BinaryWriter menu = new BinaryWriter(data))
        {
            menu.Write((ushort)0); menu.Write((ushort)0); // version, header size
            Item(menu, 0, 1, "Проверить документ");
            Item(menu, 0x80, 2, "Аккаунт NormaCAD.PRO…"); // MF_END
            using (BinaryWriter output = new BinaryWriter(File.Create(args[0])))
            {
                // Mandatory null resource at the start of a .res file.
                output.Write(new byte[] { 0,0,0,0, 32,0,0,0, 255,255,0,0, 255,255,0,0,
                    0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0 });
                Header(output, checked((int)data.Length), 4, 2000);
                output.Write(data.ToArray());
                while (output.BaseStream.Position % 4 != 0) output.Write((byte)0);
            }
        }
    }
}
