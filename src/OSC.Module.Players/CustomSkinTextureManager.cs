using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace OSC.Module.Players
{
    public sealed class QuickImportCandidate
    {
        public string FullPath;
        public string DisplayName;
    }

    public sealed class CustomSkinTextureManager
    {
        private const int MaxQuickImportCandidates = 40;
        private static readonly string[] SlotFolderNames = new string[4] { "Faces", "Shirts", "Pants", "Shoes" };

        private readonly Action<string> log;
        private readonly List<string> availableFiles = new List<string>();
        private readonly ReadOnlyCollection<string> availableFilesView;
        private readonly Dictionary<uint, Texture2D> memoryTextures = new Dictionary<uint, Texture2D>();
        private readonly Dictionary<uint, byte[]> memoryBytes = new Dictionary<uint, byte[]>();

        public string SkinsRootPath { get; private set; }
        public string TemplatesPath { get; private set; }
        public string CachePath { get; private set; }
        public string ProfilesConfigPath { get; private set; }
        public IList<string> AvailableFiles => availableFilesView;

        public CustomSkinTextureManager(Action<string> log = null)
        {
            this.log = log;
            availableFilesView = availableFiles.AsReadOnly();

            string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            SkinsRootPath = Path.GetFullPath(Path.Combine(myDocs, "MySummerCar/Mods/Assets/OurSummerCar/Skins"));
            TemplatesPath = Path.Combine(SkinsRootPath, "Templates");
            CachePath = Path.Combine(SkinsRootPath, "Cache");
            ProfilesConfigPath = Path.Combine(SkinsRootPath, "skin_profiles.cfg");
        }

        public void EnsureFoldersAndScan()
        {
            try
            {
                EnsureDirectory(SkinsRootPath);
                for (int i = 0; i < SlotFolderNames.Length; i++)
                {
                    EnsureDirectory(Path.Combine(SkinsRootPath, SlotFolderNames[i]));
                }
                EnsureDirectory(TemplatesPath);
                EnsureDirectory(CachePath);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[SkinTex] Не удалось создать папки Skins: {ex.Message}");
            }

            availableFiles.Clear();
            try
            {
                if (!Directory.Exists(SkinsRootPath)) return;
                ScanDirectoryRecursive(SkinsRootPath, string.Empty);
                availableFiles.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[SkinTex] Ошибка сканирования Skins: {ex.Message}");
            }
        }

        public bool TryPrepareLocalTexture(
            string relPath,
            CustomTexSlot slot,
            out uint crc32,
            out byte[] encodedBytes,
            out Texture2D previewTex,
            out string error)
        {
            crc32 = 0u;
            encodedBytes = null;
            previewTex = null;
            error = null;

            if (IsNullOrBlank(relPath))
            {
                error = "Путь к текстуре пуст";
                return false;
            }

            if (!TryResolveSafeRelativePath(relPath, out string fullPath, out error))
            {
                return false;
            }

            if (!File.Exists(fullPath))
            {
                error = $"Файл не найден: {relPath}";
                return false;
            }

            if (!IsSupportedImageExtension(fullPath))
            {
                error = "Поддерживаются только .png, .jpg и .jpeg";
                return false;
            }

            byte[] rawBytes;
            try
            {
                rawBytes = File.ReadAllBytes(fullPath);
            }
            catch (Exception ex)
            {
                error = $"Ошибка чтения файла: {ex.Message}";
                return false;
            }

            if (rawBytes == null || rawBytes.Length == 0)
            {
                error = "Файл изображения пуст";
                return false;
            }

            Texture2D srcTex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            Texture2D finalTex = null;
            try
            {
                if (!srcTex.LoadImage(rawBytes) || srcTex.width <= 0 || srcTex.height <= 0)
                {
                    error = "Не удалось декодировать изображение";
                    return false;
                }

                bool needsResize = srcTex.width > SkinCatalogConstants.MaxCustomTextureDimension ||
                                   srcTex.height > SkinCatalogConstants.MaxCustomTextureDimension;
                bool needsReencode = needsResize || rawBytes.Length > SkinCatalogConstants.MaxEncodedTextureBytes;

                if (!needsReencode)
                {
                    encodedBytes = rawBytes;
                    finalTex = srcTex;
                    srcTex = null;
                }
                else
                {
                    if (!TryDownscaleAndEncode(srcTex, out encodedBytes, out finalTex, out error))
                    {
                        return false;
                    }
                }

                crc32 = SkinCrc32.Compute(encodedBytes);
                if (crc32 == 0u)
                {
                    error = "Некорректная контрольная сумма текстуры";
                    return false;
                }

                memoryBytes[crc32] = encodedBytes;

                if (memoryTextures.TryGetValue(crc32, out Texture2D existing) && existing != null)
                {
                    previewTex = existing;
                }
                else
                {
                    finalTex.name = $"OSC_Skin_{crc32:X8}";
                    finalTex.wrapMode = TextureWrapMode.Clamp;
                    memoryTextures[crc32] = finalTex;
                    previewTex = finalTex;
                    finalTex = null;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Ошибка обработки текстуры ({slot}): {ex.Message}";
                return false;
            }
            finally
            {
                if (srcTex != null) UnityEngine.Object.Destroy(srcTex);
                if (finalTex != null) UnityEngine.Object.Destroy(finalTex);
            }
        }

        public void HotReloadLocalProfile(SkinPresetCollection collection)
        {
            HotReloadLocalProfileChecked(collection, out _);
        }

        public int HotReloadLocalProfileChecked(SkinPresetCollection collection, out string firstError)
        {
            firstError = null;
            if (collection == null) return 0;

            EnsureFoldersAndScan();
            var prepared = new Dictionary<string, PreparedEntry>(StringComparer.OrdinalIgnoreCase);
            bool anyChanged = false;
            int failedCount = 0;

            if (HotReloadState(collection.Active, prepared, ref failedCount, ref firstError))
            {
                anyChanged = true;
            }

            if (collection.Presets != null)
            {
                for (int i = 0; i < collection.Presets.Length; i++)
                {
                    if (HotReloadState(collection.Presets[i], prepared, ref failedCount, ref firstError))
                    {
                        anyChanged = true;
                    }
                }
            }

            if (anyChanged)
            {
                collection.Save(ProfilesConfigPath);
            }

            return failedCount;
        }

        public Texture2D GetTextureByCrc(uint crc32, bool pointFilter)
        {
            if (crc32 == 0u) return null;

            Texture2D tex;
            if (!memoryTextures.TryGetValue(crc32, out tex) || tex == null)
            {
                string cacheFile = GetCacheFilePath(crc32);
                if (string.IsNullOrEmpty(cacheFile) || !File.Exists(cacheFile))
                {
                    return null;
                }

                try
                {
                    byte[] data = File.ReadAllBytes(cacheFile);
                    if (data == null || data.Length == 0 || data.Length > SkinCatalogConstants.MaxEncodedTextureBytes)
                    {
                        return null;
                    }
                    if (SkinCrc32.Compute(data) != crc32)
                    {
                        return null;
                    }

                    tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                    if (!tex.LoadImage(data))
                    {
                        UnityEngine.Object.Destroy(tex);
                        return null;
                    }
                    tex.name = $"OSC_Skin_{crc32:X8}";
                    tex.wrapMode = TextureWrapMode.Clamp;
                    memoryTextures[crc32] = tex;
                    memoryBytes[crc32] = data;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[SkinTex] Ошибка чтения кэша {crc32:X8}: {ex.Message}");
                    return null;
                }
            }

            if (tex != null)
            {
                FilterMode target = pointFilter ? FilterMode.Point : FilterMode.Bilinear;
                if (tex.filterMode != target) tex.filterMode = target;
            }
            return tex;
        }

        public bool TryGetTextureBytes(uint crc32, out byte[] data)
        {
            data = null;
            if (crc32 == 0u) return false;

            if (memoryBytes.TryGetValue(crc32, out byte[] cached) &&
                cached != null &&
                cached.Length > 0 &&
                cached.Length <= SkinCatalogConstants.MaxEncodedTextureBytes)
            {
                data = cached;
                return true;
            }

            string cacheFile = GetCacheFilePath(crc32);
            if (!string.IsNullOrEmpty(cacheFile) && File.Exists(cacheFile))
            {
                try
                {
                    byte[] diskBytes = File.ReadAllBytes(cacheFile);
                    if (diskBytes != null &&
                        diskBytes.Length > 0 &&
                        diskBytes.Length <= SkinCatalogConstants.MaxEncodedTextureBytes &&
                        SkinCrc32.Compute(diskBytes) == crc32)
                    {
                        memoryBytes[crc32] = diskBytes;
                        data = diskBytes;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[SkinTex] Ошибка чтения байтов кэша {crc32:X8}: {ex.Message}");
                }
            }

            return false;
        }

        public bool StoreRemoteTextureBytes(uint crc32, byte[] data, bool pointFilter, out Texture2D tex)
        {
            tex = null;
            if (crc32 == 0u || data == null || data.Length == 0 || data.Length > SkinCatalogConstants.MaxEncodedTextureBytes)
            {
                return false;
            }
            if (SkinCrc32.Compute(data) != crc32)
            {
                return false;
            }

            if (memoryTextures.TryGetValue(crc32, out Texture2D existing) && existing != null)
            {
                tex = existing;
            }
            else
            {
                tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                if (!tex.LoadImage(data))
                {
                    UnityEngine.Object.Destroy(tex);
                    tex = null;
                    return false;
                }
                tex.name = $"OSC_Skin_{crc32:X8}";
                tex.wrapMode = TextureWrapMode.Clamp;
                memoryTextures[crc32] = tex;
            }

            tex.filterMode = pointFilter ? FilterMode.Point : FilterMode.Bilinear;
            memoryBytes[crc32] = data;

            try
            {
                EnsureDirectory(CachePath);
                string cacheFile = GetCacheFilePath(crc32);
                if (!File.Exists(cacheFile))
                {
                    File.WriteAllBytes(cacheFile, data);
                }
                else
                {
                    File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow);
                }
                PruneCacheIfNeeded();
            }
            catch (Exception ex)
            {
                log?.Invoke($"[SkinTex] Ошибка записи кэша {crc32:X8}: {ex.Message}");
            }

            return true;
        }

        public int ExportUvTemplates()
        {
            if (!CharacterAssetLoader.IsLoaded)
            {
                CharacterAssetLoader.LoadAssets(log);
            }

            EnsureDirectory(TemplatesPath);
            int exported = 0;

            exported += TryExportSingleTemplate(GetFirstTexture(CharacterAssetLoader.Faces), "template_face.png") ? 1 : 0;
            exported += TryExportSingleTemplate(GetFirstTexture(CharacterAssetLoader.Shirts), "template_shirt.png") ? 1 : 0;
            exported += TryExportSingleTemplate(GetFirstTexture(CharacterAssetLoader.Pants), "template_pants.png") ? 1 : 0;
            exported += TryExportSingleTemplate(GetFirstTexture(CharacterAssetLoader.Shoes), "template_shoes.png") ? 1 : 0;

            return exported;
        }

        public void OpenSkinsFolder()
        {
            try
            {
                EnsureDirectory(SkinsRootPath);
                Process.Start("explorer.exe", $"\"{SkinsRootPath}\"");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[SkinTex] Не удалось открыть папку Skins: {ex.Message}");
            }
        }

        public List<QuickImportCandidate> ScanQuickImportSources()
        {
            var collected = new List<QuickImportFileEntry>();

            AddSourceCandidates(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Desktop", collected);
            string userProfile = null;
            try { userProfile = Environment.GetEnvironmentVariable("USERPROFILE"); } catch { }
            if (IsNullOrBlank(userProfile))
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!IsNullOrBlank(docs)) userProfile = Path.GetDirectoryName(docs);
            }
            if (!IsNullOrBlank(userProfile))
            {
                AddSourceCandidates(Path.Combine(userProfile, "Downloads"), "Downloads", collected);
            }
            AddSourceCandidates(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pictures", collected);

            collected.Sort((a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc));

            int count = Math.Min(collected.Count, MaxQuickImportCandidates);
            var result = new List<QuickImportCandidate>(count);
            for (int i = 0; i < count; i++)
            {
                result.Add(new QuickImportCandidate
                {
                    FullPath = collected[i].FullPath,
                    DisplayName = collected[i].DisplayName
                });
            }
            return result;
        }

        public bool TryImportExternalFile(string fullSourcePath, CustomTexSlot slot, out string importedRelPath, out string error)
        {
            importedRelPath = string.Empty;
            error = null;

            if (IsNullOrBlank(fullSourcePath))
            {
                error = "Путь к файлу не указан";
                return false;
            }

            string cleanPath = fullSourcePath.Trim().Trim('"');
            if (IsNullOrBlank(cleanPath) || !File.Exists(cleanPath))
            {
                error = "Исходный файл не найден";
                return false;
            }

            if (!IsSupportedImageExtension(cleanPath))
            {
                error = "Выберите файл .png, .jpg или .jpeg";
                return false;
            }

            try
            {
                string slotFolder = GetSlotFolderName(slot);
                string targetDir = Path.Combine(SkinsRootPath, slotFolder);
                EnsureDirectory(targetDir);

                string origName = Path.GetFileName(cleanPath);
                if (IsNullOrBlank(origName))
                {
                    error = "Некорректное имя файла";
                    return false;
                }

                string destPath = BuildUniqueDestinationPath(targetDir, origName);
                File.Copy(cleanPath, destPath, false);

                string destFileName = Path.GetFileName(destPath);
                importedRelPath = slotFolder + "/" + destFileName;
                EnsureFoldersAndScan();
                return true;
            }
            catch (Exception ex)
            {
                error = $"Ошибка импорта: {ex.Message}";
                return false;
            }
        }

        public void DisposeTextures()
        {
            foreach (KeyValuePair<uint, Texture2D> kv in memoryTextures)
            {
                if (kv.Value != null)
                {
                    UnityEngine.Object.Destroy(kv.Value);
                }
            }
            memoryTextures.Clear();
            memoryBytes.Clear();
        }

        private bool HotReloadState(PlayerAppearanceState state, Dictionary<string, PreparedEntry> prepared, ref int failedCount, ref string firstError)
        {
            if (state == null) return false;

            bool changed = false;
            for (int i = 0; i < 4; i++)
            {
                CustomTexSlot slot = (CustomTexSlot)i;
                string relPath = state.CustomRelativePaths[i];
                if (IsNullOrBlank(relPath) || !state.HasCustomTexture(slot))
                {
                    continue;
                }

                string cacheKey = $"{i}|{relPath}";
                if (!prepared.TryGetValue(cacheKey, out PreparedEntry entry))
                {
                    if (TryPrepareLocalTexture(relPath, slot, out uint crc, out byte[] bytes, out Texture2D tex, out string err))
                    {
                        entry = new PreparedEntry
                        {
                            Success = true,
                            Crc32 = crc,
                            ByteLength = bytes.Length,
                            Texture = tex
                        };
                    }
                    else
                    {
                        entry = new PreparedEntry { Success = false, Error = err };
                    }
                    prepared[cacheKey] = entry;
                }

                if (entry.Success)
                {
                    if (state.CustomCrc32[i] != entry.Crc32 || state.CustomByteLengths[i] != entry.ByteLength)
                    {
                        state.SetCustomTexture(slot, relPath, entry.Crc32, entry.ByteLength);
                        changed = true;
                    }
                    if (entry.Texture != null)
                    {
                        FilterMode target = state.IsPointFiltered(slot) ? FilterMode.Point : FilterMode.Bilinear;
                        if (entry.Texture.filterMode != target) entry.Texture.filterMode = target;
                    }
                }
                else
                {
                    failedCount++;
                    if (firstError == null)
                    {
                        firstError = $"{relPath}: {entry.Error}";
                    }
                    log?.Invoke($"[SkinTex] Горячая перезагрузка ({relPath}): {entry.Error}");
                }
            }
            return changed;
        }

        private bool TryDownscaleAndEncode(Texture2D srcTex, out byte[] encodedBytes, out Texture2D finalTex, out string error)
        {
            encodedBytes = null;
            finalTex = null;
            error = null;

            bool hasAlpha = HasTransparency(srcTex);
            int maxDim = Math.Max(srcTex.width, srcTex.height);
            int targetW = srcTex.width;
            int targetH = srcTex.height;

            if (maxDim > SkinCatalogConstants.MaxCustomTextureDimension)
            {
                float scale = (float)SkinCatalogConstants.MaxCustomTextureDimension / maxDim;
                targetW = Math.Max(1, Mathf.RoundToInt(srcTex.width * scale));
                targetH = Math.Max(1, Mathf.RoundToInt(srcTex.height * scale));
            }

            int quality = SkinCatalogConstants.JpgQuality;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Texture2D candidate;
                if (targetW == srcTex.width && targetH == srcTex.height)
                {
                    candidate = CloneReadableTexture(srcTex);
                }
                else
                {
                    candidate = BlitResizeTexture(srcTex, targetW, targetH);
                }

                byte[] bytes = hasAlpha ? candidate.EncodeToPNG() : candidate.EncodeToJPG(quality);
                if (bytes != null && bytes.Length > 0 && bytes.Length <= SkinCatalogConstants.MaxEncodedTextureBytes)
                {
                    encodedBytes = bytes;
                    finalTex = candidate;
                    return true;
                }

                UnityEngine.Object.Destroy(candidate);

                if (!hasAlpha && quality > 74)
                {
                    quality -= 8;
                    continue;
                }

                if (targetW <= 64 && targetH <= 64)
                {
                    break;
                }

                targetW = Math.Max(64, (int)(targetW * 0.75f));
                targetH = Math.Max(64, (int)(targetH * 0.75f));
            }

            error = $"Не удалось сжать текстуру до {SkinCatalogConstants.MaxEncodedTextureBytes / 1024} КБ";
            return false;
        }

        private static bool HasTransparency(Texture2D tex)
        {
            Color32[] pixels = tex.GetPixels32();
            if (pixels == null) return false;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a < 250) return true;
            }
            return false;
        }

        private static Texture2D CloneReadableTexture(Texture2D src)
        {
            Texture2D copy = new Texture2D(src.width, src.height, TextureFormat.ARGB32, false);
            copy.SetPixels32(src.GetPixels32());
            copy.Apply(false, false);
            return copy;
        }

        private static Texture2D BlitResizeTexture(Texture2D src, int width, int height)
        {
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            Texture2D result = null;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                result = new Texture2D(width, height, TextureFormat.ARGB32, false);
                result.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                result.Apply(false, false);
                return result;
            }
            catch
            {
                if (result != null) UnityEngine.Object.Destroy(result);
                throw;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private bool TryExportSingleTemplate(Texture2D src, string fileName)
        {
            if (src == null) return false;

            string destPath = Path.Combine(TemplatesPath, fileName);
            Texture2D readable = null;
            try
            {
                readable = BlitResizeTexture(src, src.width, src.height);
                byte[] png = readable.EncodeToPNG();
                if (png != null && png.Length > 0)
                {
                    File.WriteAllBytes(destPath, png);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[SkinTex] Ошибка экспорта шаблона {fileName}: {ex.Message}");
                return false;
            }
            finally
            {
                if (readable != null) UnityEngine.Object.Destroy(readable);
            }
        }

        private void ScanDirectoryRecursive(string dirPath, string relPrefix)
        {
            string[] files = Directory.GetFiles(dirPath);
            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                if (!IsSupportedImageExtension(file)) continue;
                string name = Path.GetFileName(file);
                string rel = string.IsNullOrEmpty(relPrefix) ? name : (relPrefix + "/" + name);
                availableFiles.Add(rel);
            }

            string[] subDirs = Directory.GetDirectories(dirPath);
            for (int i = 0; i < subDirs.Length; i++)
            {
                string sub = subDirs[i];
                string dirName = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(dirName)) continue;
                if (string.Equals(dirName, "Templates", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dirName, "Cache", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string nextPrefix = string.IsNullOrEmpty(relPrefix) ? dirName : (relPrefix + "/" + dirName);
                ScanDirectoryRecursive(sub, nextPrefix);
            }
        }

        private bool TryResolveSafeRelativePath(string relPath, out string fullPath, out string error)
        {
            fullPath = null;
            error = null;

            string normalizedRel = relPath.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedRel))
            {
                error = "Укажите относительный путь внутри папки Skins";
                return false;
            }

            try
            {
                string rootWithSep = SkinsRootPath.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? SkinsRootPath
                    : (SkinsRootPath + Path.DirectorySeparatorChar);
                fullPath = Path.GetFullPath(Path.Combine(SkinsRootPath, normalizedRel));
                if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    fullPath = null;
                    error = "Недопустимый путь вне папки Skins";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = $"Некорректный путь: {ex.Message}";
                return false;
            }
        }

        private void PruneCacheIfNeeded()
        {
            if (!Directory.Exists(CachePath)) return;
            string[] files = Directory.GetFiles(CachePath, "*.bin");
            if (files == null || files.Length <= SkinCatalogConstants.MaxCachedFiles) return;

            var infos = new List<FileInfo>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                infos.Add(new FileInfo(files[i]));
            }
            infos.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

            int toDelete = infos.Count - SkinCatalogConstants.MaxCachedFiles;
            for (int i = 0; i < toDelete; i++)
            {
                try { infos[i].Delete(); }
                catch { }
            }
        }

        private void AddSourceCandidates(string folderPath, string label, List<QuickImportFileEntry> target)
        {
            if (IsNullOrBlank(folderPath) || !Directory.Exists(folderPath)) return;

            try
            {
                string[] files = Directory.GetFiles(folderPath);
                for (int i = 0; i < files.Length; i++)
                {
                    string f = files[i];
                    if (!IsSupportedImageExtension(f)) continue;
                    try
                    {
                        DateTime writeUtc = File.GetLastWriteTimeUtc(f);
                        target.Add(new QuickImportFileEntry
                        {
                            FullPath = f,
                            DisplayName = $"[{label}] {Path.GetFileName(f)}",
                            LastWriteUtc = writeUtc
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }

        private string GetCacheFilePath(uint crc32)
        {
            return Path.Combine(CachePath, crc32.ToString("X8") + ".bin");
        }

        private static string GetSlotFolderName(CustomTexSlot slot)
        {
            int idx = (int)slot;
            if (idx >= 0 && idx < SlotFolderNames.Length) return SlotFolderNames[idx];
            return "Shirts";
        }

        private static string BuildUniqueDestinationPath(string dir, string fileName)
        {
            string candidate = Path.Combine(dir, fileName);
            if (!File.Exists(candidate)) return candidate;

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            for (int i = 1; i <= 999; i++)
            {
                candidate = Path.Combine(dir, $"{baseName}_{i}{ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, $"{baseName}_{Guid.NewGuid().ToString("N").Substring(0, 6)}{ext}");
        }

        private static Texture2D GetFirstTexture(Texture2D[] arr)
        {
            return (arr != null && arr.Length > 0) ? arr[0] : null;
        }

        private static void EnsureDirectory(string path)
        {
            if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        private static bool IsSupportedImageExtension(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            return string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNullOrBlank(string value)
        {
            return value == null || value.Trim().Length == 0;
        }

        private sealed class PreparedEntry
        {
            public bool Success;
            public uint Crc32;
            public int ByteLength;
            public Texture2D Texture;
            public string Error;
        }

        private sealed class QuickImportFileEntry
        {
            public string FullPath;
            public string DisplayName;
            public DateTime LastWriteUtc;
        }
    }
}
