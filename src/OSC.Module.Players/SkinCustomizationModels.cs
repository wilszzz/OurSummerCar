using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace OSC.Module.Players
{
    public static class AppearanceSubPacket
    {
        public const byte AppearanceStateSync = 1;
        public const byte CustomTextureRequest = 2;
        public const byte CustomTextureChunk = 3;
        public const byte CustomTextureAckOrClear = 4;
    }

    public enum ClothingSlot : byte
    {
        Glasses = 0,
        Headgear = 1,
        Face = 2,
        Shirt = 3,
        Pants = 4,
        Shoes = 5
    }

    public enum CustomTexSlot : byte
    {
        Face = 0,
        Shirt = 1,
        Pants = 2,
        Shoes = 3
    }

    public static class SkinCatalogConstants
    {
        public const int PresetSlotCount = 5;
        public const int NumBuiltInFaces = 17;
        public const int NumBuiltInPants = 21;
        public const int NumBuiltInShirts = 47;
        public const int NumBuiltInShoes = 7;

        public const int MaxCustomTextureDimension = 1024;
        public const int MaxEncodedTextureBytes = 512 * 1024;
        public const int PreferJpgThresholdBytes = 384 * 1024;
        public const int JpgQuality = 88;
        public const int ChunkSizeBytes = 16 * 1024;
        public const int MaxCachedFiles = 40;

        public static readonly string[] SlotDisplayNamesRu = new string[6]
        {
            "Очки",
            "Головной убор / Волосы",
            "Лицо",
            "Футболка / Верх",
            "Штаны / Шорты",
            "Обувь"
        };

        public static readonly string[] CustomSlotNamesRu = new string[4]
        {
            "Лицо",
            "Футболка / Верх",
            "Штаны / Шорты",
            "Обувь"
        };

        public static readonly string[] NamesShirts = new string[47]
        {
            "None", "Teimo", "Gifu", "Yellow", "Grey", "Teal", "Van craze", "White shirt", "Black", "Blue amis",
            "Arvo", "Office", "Yellow FUstreet", "Black shirt", "Green shirt", "Suski", "White shirt 2", "BP top", "White shirt 3", "Blue shirt",
            "Maceeiko", "Black shirt 2", "Maceeiko 2", "Kekmet", "Balfield", "White FUstreet", "RCO", "CORRIS", "Voittous", "Slate blue",
            "Teal shirt", "Satsuma shirt", "Yellow amis", "CORRIS 2", "Polaventris", "Hayosiko", "Hayosiko 2", "CORRIS 3", "Suvi sprint 1992", "Talvi sprint 1993",
            "Office 2", "Blue shirt", "Mafia shirt", "Dassler shirt", "Black shirt 3", "Cop", "Cop 2"
        };

        public static readonly string[] NamesPants = new string[21]
        {
            "Blue", "Checked black", "Stripped blue", "Beige", "Mirrored shorts", "Blue jeans", "BP pants", "Black", "Hayosiko shorts", "Blue with belt",
            "Teal", "Checked brown", "Checked blue", "Checked green", "Checked orange", "Checked red", "Green shorts", "Red shorts", "Stripped black", "Cop jeans",
            "White shorts"
        };

        public static CustomTexSlot? ClothingToCustomSlot(ClothingSlot slot)
        {
            switch (slot)
            {
                case ClothingSlot.Face: return CustomTexSlot.Face;
                case ClothingSlot.Shirt: return CustomTexSlot.Shirt;
                case ClothingSlot.Pants: return CustomTexSlot.Pants;
                case ClothingSlot.Shoes: return CustomTexSlot.Shoes;
                default: return null;
            }
        }
    }

    public sealed class PlayerAppearanceState
    {
        public readonly byte[] SlotIndices = new byte[6] { 0, 0, 6, 12, 4, 3 };
        public byte FilterPointMask;
        public byte CustomSlotMask;

        public readonly string[] CustomRelativePaths = new string[4] { string.Empty, string.Empty, string.Empty, string.Empty };
        public readonly uint[] CustomCrc32 = new uint[4];
        public readonly int[] CustomByteLengths = new int[4];

        public bool IsPointFiltered(CustomTexSlot slot)
        {
            return (FilterPointMask & (1 << (int)slot)) != 0;
        }

        public void SetPointFiltered(CustomTexSlot slot, bool point)
        {
            byte bit = (byte)(1 << (int)slot);
            if (point) FilterPointMask |= bit;
            else FilterPointMask &= (byte)~bit;
        }

        public bool HasCustomTexture(CustomTexSlot slot)
        {
            int idx = (int)slot;
            return (CustomSlotMask & (1 << idx)) != 0 && CustomCrc32[idx] != 0;
        }

        public void SetCustomTexture(CustomTexSlot slot, string relPath, uint crc32, int byteLen)
        {
            int idx = (int)slot;
            if (crc32 == 0 || byteLen <= 0)
            {
                ClearCustomTexture(slot);
                return;
            }
            CustomSlotMask |= (byte)(1 << idx);
            CustomRelativePaths[idx] = relPath ?? string.Empty;
            CustomCrc32[idx] = crc32;
            CustomByteLengths[idx] = byteLen;
        }

        public void ClearCustomTexture(CustomTexSlot slot)
        {
            int idx = (int)slot;
            CustomSlotMask &= (byte)~(1 << idx);
            CustomRelativePaths[idx] = string.Empty;
            CustomCrc32[idx] = 0;
            CustomByteLengths[idx] = 0;
        }

        public void ResetToDefaults()
        {
            SlotIndices[0] = 0;
            SlotIndices[1] = 0;
            SlotIndices[2] = 6;
            SlotIndices[3] = 12;
            SlotIndices[4] = 4;
            SlotIndices[5] = 3;
            FilterPointMask = 0;
            CustomSlotMask = 0;
            for (int i = 0; i < 4; i++)
            {
                CustomRelativePaths[i] = string.Empty;
                CustomCrc32[i] = 0;
                CustomByteLengths[i] = 0;
            }
        }

        public PlayerAppearanceState Clone()
        {
            var copy = new PlayerAppearanceState();
            for (int i = 0; i < 6; i++) copy.SlotIndices[i] = SlotIndices[i];
            copy.FilterPointMask = FilterPointMask;
            copy.CustomSlotMask = CustomSlotMask;
            for (int i = 0; i < 4; i++)
            {
                copy.CustomRelativePaths[i] = CustomRelativePaths[i];
                copy.CustomCrc32[i] = CustomCrc32[i];
                copy.CustomByteLengths[i] = CustomByteLengths[i];
            }
            return copy;
        }

        public void CopyFrom(PlayerAppearanceState src)
        {
            if (src == null) return;
            for (int i = 0; i < 6; i++) SlotIndices[i] = src.SlotIndices[i];
            FilterPointMask = src.FilterPointMask;
            CustomSlotMask = src.CustomSlotMask;
            for (int i = 0; i < 4; i++)
            {
                CustomRelativePaths[i] = src.CustomRelativePaths[i];
                CustomCrc32[i] = src.CustomCrc32[i];
                CustomByteLengths[i] = src.CustomByteLengths[i];
            }
        }

        public bool WireEquals(PlayerAppearanceState other)
        {
            if (other == null) return false;
            for (int i = 0; i < 6; i++)
            {
                if (SlotIndices[i] != other.SlotIndices[i]) return false;
            }
            if (FilterPointMask != other.FilterPointMask || CustomSlotMask != other.CustomSlotMask) return false;
            for (int i = 0; i < 4; i++)
            {
                bool a = HasCustomTexture((CustomTexSlot)i);
                bool b = other.HasCustomTexture((CustomTexSlot)i);
                if (a != b) return false;
                if (a && (CustomCrc32[i] != other.CustomCrc32[i] || CustomByteLengths[i] != other.CustomByteLengths[i]))
                {
                    return false;
                }
            }
            return true;
        }
    }

    public sealed class SkinPresetCollection
    {
        public readonly PlayerAppearanceState Active = new PlayerAppearanceState();
        public readonly PlayerAppearanceState[] Presets = new PlayerAppearanceState[SkinCatalogConstants.PresetSlotCount];
        public int ActivePresetIndex = -1;

        public SkinPresetCollection()
        {
            for (int i = 0; i < Presets.Length; i++)
            {
                Presets[i] = new PlayerAppearanceState();
            }
        }

        public void Save(string filePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("active_preset=" + ActivePresetIndex.ToString(CultureInfo.InvariantCulture));
                AppendState(sb, "active", Active);
                for (int i = 0; i < Presets.Length; i++)
                {
                    AppendState(sb, "p" + i.ToString(CultureInfo.InvariantCulture), Presets[i]);
                }
                File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public void Load(string filePath, Action<string> log = null)
        {
            Active.ResetToDefaults();
            for (int i = 0; i < Presets.Length; i++) Presets[i].ResetToDefaults();
            ActivePresetIndex = -1;

            bool loadedCfg = false;
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try
                {
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        if (string.IsNullOrEmpty(line)) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        string val = line.Substring(eq + 1).Trim();
                        dict[key] = val;
                    }

                    if (dict.TryGetValue("active_preset", out string apStr) &&
                        int.TryParse(apStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ap))
                    {
                        ActivePresetIndex = ap;
                    }
                    ReadState(dict, "active", Active);
                    for (int i = 0; i < Presets.Length; i++)
                    {
                        ReadState(dict, "p" + i.ToString(CultureInfo.InvariantCulture), Presets[i]);
                    }
                    loadedCfg = true;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[SkinConfig] Ошибка чтения профиля: {ex.Message}");
                }
            }

            if (!loadedCfg)
            {
                Save(filePath);
            }
        }

        private static void AppendState(StringBuilder sb, string prefix, PlayerAppearanceState st)
        {
            sb.Append(prefix).Append("_slots=");
            for (int i = 0; i < 6; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(st.SlotIndices[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine();
            sb.Append(prefix).Append("_filter=").Append(st.FilterPointMask.ToString(CultureInfo.InvariantCulture)).AppendLine();
            for (int i = 0; i < 4; i++)
            {
                if (st.HasCustomTexture((CustomTexSlot)i) || !string.IsNullOrEmpty(st.CustomRelativePaths[i]))
                {
                    sb.Append(prefix).Append("_tex").Append(i.ToString(CultureInfo.InvariantCulture)).Append('=')
                      .Append((st.CustomRelativePaths[i] ?? string.Empty).Replace("|", "_"))
                      .Append('|').Append(st.CustomCrc32[i].ToString(CultureInfo.InvariantCulture))
                      .Append('|').Append(st.CustomByteLengths[i].ToString(CultureInfo.InvariantCulture))
                      .AppendLine();
                }
            }
        }

        private static void ReadState(Dictionary<string, string> dict, string prefix, PlayerAppearanceState st)
        {
            if (dict.TryGetValue(prefix + "_slots", out string slotsStr))
            {
                string[] parts = slotsStr.Split(',');
                for (int i = 0; i < 6 && i < parts.Length; i++)
                {
                    if (byte.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte v))
                    {
                        st.SlotIndices[i] = v;
                    }
                }
            }
            if (dict.TryGetValue(prefix + "_filter", out string filterStr) &&
                byte.TryParse(filterStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte fMask))
            {
                st.FilterPointMask = fMask;
            }
            for (int i = 0; i < 4; i++)
            {
                if (dict.TryGetValue(prefix + "_tex" + i.ToString(CultureInfo.InvariantCulture), out string texLine))
                {
                    string[] p = texLine.Split('|');
                    if (p.Length >= 3 &&
                        uint.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint crc) &&
                        int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int len))
                    {
                        st.SetCustomTexture((CustomTexSlot)i, p[0], crc, len);
                    }
                }
            }
        }
    }

    public static class SkinCrc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            const uint poly = 0xEDB88320u;
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = ((c & 1u) != 0) ? (poly ^ (c >> 1)) : (c >> 1);
                }
                table[i] = c;
            }
            return table;
        }

        public static uint Compute(byte[] data)
        {
            if (data == null || data.Length == 0) return 0u;
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
            {
                crc = Table[(crc ^ data[i]) & 0xFFu] ^ (crc >> 8);
            }
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
