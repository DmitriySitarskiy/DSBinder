using System.Text;

namespace DSBinder.Core.Models
{
    /// <summary>Людські назви віртуальних клавіш Windows.</summary>
    public static class KeyNames
    {
        public static string Format(uint vk, bool ctrl, bool shift, bool alt)
        {
            if (vk == 0) return "—";

            var sb = new StringBuilder();
            if (ctrl) sb.Append("Ctrl + ");
            if (alt) sb.Append("Alt + ");
            if (shift) sb.Append("Shift + ");
            sb.Append(KeyName(vk));
            return sb.ToString();
        }

        public static string KeyName(uint vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();             // 0-9
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();             // A-Z
            if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);              // NumPad 0-9
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);                 // F1-F24

            return vk switch
            {
                0x01 => "ЛКМ",
                0x02 => "ПКМ",
                0x04 => "СКМ",
                0x05 => "Mouse 4",
                0x06 => "Mouse 5",
                0x08 => "Backspace",
                0x09 => "Tab",
                0x0C => "Clear",
                0x0D => "Enter",
                0x13 => "Pause",
                0x14 => "CapsLock",
                0x1B => "Esc",
                0x20 => "Space",
                0x21 => "PgUp",
                0x22 => "PgDn",
                0x23 => "End",
                0x24 => "Home",
                0x25 => "←",
                0x26 => "↑",
                0x27 => "→",
                0x28 => "↓",
                0x2C => "PrtSc",
                0x2D => "Insert",
                0x2E => "Delete",
                0x5D => "Menu",
                0x6A => "Num *",
                0x6B => "Num +",
                0x6C => "Num Enter",
                0x6D => "Num -",
                0x6E => "Num .",
                0x6F => "Num /",
                0x90 => "NumLock",
                0x91 => "ScrollLock",
                0xBA => ";",
                0xBB => "=",
                0xBC => ",",
                0xBD => "-",
                0xBE => ".",
                0xBF => "/",
                0xC0 => "`",
                0xDB => "[",
                0xDC => "\\",
                0xDD => "]",
                0xDE => "'",
                0xE2 => "\\ (102)",
                _ => $"VK {vk:X2}"
            };
        }
    }
}
