using System.Drawing;

namespace KiCadSolderingAssistant
{
    // Shared semantic roles keep native controls independent of a fixed skin.
    internal sealed class Palette
    {
        internal string Id, Name;
        internal Color Background, Surface, Soft, Border, Ink, Muted, Accent, Tint;
        internal static readonly string[] Ids = { "peach", "blue", "lilac" };
        internal static readonly string[] Names = { "暖白珊瑚", "雾蓝", "浅紫" };
        static Color Hex(string value) { return ColorTranslator.FromHtml(value); }
        internal static Palette Get(string id)
        {
            int index = System.Array.IndexOf(Ids, id);
            if (index < 0) index = 0;
            string[][] colors = {
                new[] { "#FAF7F2", "#FFFDFA", "#F3EAE1", "#DFD2C8", "#443B38", "#71635D", "#A34A37", "#F8E5DB" },
                new[] { "#F3F6FA", "#FCFDFF", "#E8EFF7", "#CCD9E8", "#34445A", "#596B81", "#3B649D", "#E0EAF9" },
                new[] { "#F8F5FA", "#FFFDFE", "#EFE8F5", "#DCD0E6", "#4B3D59", "#715F7E", "#80559D", "#EFE1F7" }
            };
            var c = colors[index];
            return new Palette { Id = Ids[index], Name = Names[index], Background = Hex(c[0]), Surface = Hex(c[1]), Soft = Hex(c[2]),
                Border = Hex(c[3]), Ink = Hex(c[4]), Muted = Hex(c[5]), Accent = Hex(c[6]), Tint = Hex(c[7]) };
        }
    }
}
