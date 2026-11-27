using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace OSC.Module.Players
{
    public sealed class SkinWardrobeStudioUI
    {
        private const int WindowId = 99212;
        private const int PreviewWidth = 384;
        private const int PreviewHeight = 512;
        private static readonly Vector3 RigPosition = new Vector3(0f, -500f, 0f);
        private static readonly string[] CustomTabSlotLabels = new string[4] { "Лицо", "Футболка", "Штаны", "Обувь" };

        private readonly SkinPresetCollection profiles;
        private readonly CustomSkinTextureManager texManager;
        private readonly Action onAppearanceChanged;
        private readonly Action<string> log;
        private readonly System.Random rng = new System.Random();

        private bool isVisible;
        private int activeTab; // 0 = Внешность и Пресеты, 1 = Свои Текстуры
        private CustomTexSlot selectedCustomSlot = CustomTexSlot.Shirt;
        private bool zoomFace;
        private float previewYaw;
        private bool isDraggingPreview;

        private Rect windowRect;
        private bool windowRectInitialized;
        private Vector2 slotsScrollPos = Vector2.zero;
        private Vector2 filesScrollPos = Vector2.zero;
        private Vector2 quickImportScrollPos = Vector2.zero;

        private string manualImportPath = string.Empty;
        private string statusMessage = string.Empty;
        private bool statusIsError;
        private List<QuickImportCandidate> quickImportCandidates = new List<QuickImportCandidate>();
        private bool quickImportScannedOnce;

        private GameObject previewRoot;
        private GameObject previewModel;
        private Camera previewCamera;
        private RenderTexture previewTexture;
        private SkinnedMeshRenderer previewBodyMesh;
        private Transform previewHeadTransform;
        private bool previewNeedsFraming;

        public bool IsVisible
        {
            get { return isVisible; }
            set
            {
                if (isVisible == value) return;
                isVisible = value;
                if (isVisible)
                {
                    texManager?.EnsureFoldersAndScan();
                    if (!quickImportScannedOnce && texManager != null)
                    {
                        quickImportScannedOnce = true;
                        quickImportCandidates = texManager.ScanQuickImportSources() ?? new List<QuickImportCandidate>();
                    }
                    EnsurePreviewRig();
                    SetPreviewRigActive(true);
                    RefreshPreview();
                }
                else
                {
                    isDraggingPreview = false;
                    SetPreviewRigActive(false);
                }
            }
        }

        public SkinWardrobeStudioUI(
            SkinPresetCollection profiles,
            CustomSkinTextureManager texManager,
            Action onAppearanceChanged,
            Action<string> log)
        {
            this.profiles = profiles;
            this.texManager = texManager;
            this.onAppearanceChanged = onAppearanceChanged;
            this.log = log;
        }

        public void Toggle()
        {
            IsVisible = !IsVisible;
        }

        public void OnUpdate()
        {
            if (!isVisible) return;

            if (previewRoot == null || previewCamera == null || previewTexture == null)
            {
                EnsurePreviewRig();
                SetPreviewRigActive(true);
                RefreshPreview();
            }

            if (previewModel != null)
            {
                previewModel.transform.localEulerAngles = new Vector3(0f, previewYaw, 0f);
                if (previewNeedsFraming)
                {
                    ApplyCameraFraming();
                    previewNeedsFraming = false;
                }
            }
        }

        public void OnGUI()
        {
            if (!isVisible) return;

            if (!windowRectInitialized)
            {
                float w = Mathf.Min(Screen.width - 40f, 840f);
                float h = Mathf.Min(Screen.height - 40f, 610f);
                windowRect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
                windowRectInitialized = true;
            }

            GUI.depth = -110;
            windowRect.width = Mathf.Min(Screen.width - 20f, 840f);
            windowRect.height = Mathf.Min(Screen.height - 20f, 610f);
            windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "Гардероб и Свой Скин — OurSummerCar (F8 / F9)");
        }

        public void RefreshPreview()
        {
            if (!isVisible && previewModel == null) return;

            if (isVisible && (previewRoot == null || previewModel == null || previewTexture == null))
            {
                EnsurePreviewRig();
                SetPreviewRigActive(isVisible);
            }

            if (previewModel != null && profiles != null)
            {
                CharacterAssetLoader.ApplyAppearance(previewModel, profiles.Active, texManager, false, log);
                previewNeedsFraming = true;
                ApplyCameraFraming();
            }
        }

        public void Cleanup()
        {
            isVisible = false;
            isDraggingPreview = false;

            if (previewCamera != null)
            {
                previewCamera.targetTexture = null;
            }
            if (previewTexture != null)
            {
                previewTexture.Release();
                UnityEngine.Object.Destroy(previewTexture);
                previewTexture = null;
            }
            if (previewModel != null)
            {
                UnityEngine.Object.Destroy(previewModel);
                previewModel = null;
            }
            if (previewRoot != null)
            {
                UnityEngine.Object.Destroy(previewRoot);
                previewRoot = null;
            }

            previewCamera = null;
            previewBodyMesh = null;
            previewHeadTransform = null;
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            DrawPreviewColumn();
            GUILayout.Space(8f);
            DrawControlsColumn();

            GUILayout.EndHorizontal();
            GUI.DragWindow();
        }

        private void DrawPreviewColumn()
        {
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(272f), GUILayout.ExpandHeight(true));
            GUILayout.Label("<b>3D Предпросмотр</b>");

            if (previewTexture != null && previewModel != null)
            {
                GUILayout.Box(previewTexture, GUILayout.Width(256f), GUILayout.Height(340f));
                Rect previewRect = GUILayoutUtility.GetLastRect();
                HandlePreviewMouseDrag(previewRect);
            }
            else
            {
                GUILayout.Box("3D-модель не загружена\n(проверьте clothes.unity3d)", GUILayout.Width(256f), GUILayout.Height(340f));
            }

            GUILayout.Label($"Поворот: {Mathf.RoundToInt(previewYaw)}° (тяните мышью)");
            GUILayout.BeginHorizontal();
            float nextYaw = GUILayout.HorizontalSlider(previewYaw, 0f, 360f);
            if (Mathf.Abs(nextYaw - previewYaw) > 0.01f)
            {
                previewYaw = WrapAngle360(nextYaw);
            }
            if (GUILayout.Button("0°", GUILayout.Width(36f)))
            {
                previewYaw = 0f;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            GUILayout.Label("<b>Камера:</b>");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(zoomFace ? "Весь рост" : "[ Весь рост ]"))
            {
                zoomFace = false;
                ApplyCameraFraming();
            }
            if (GUILayout.Button(zoomFace ? "[ Лицо / Шапка ]" : "Лицо / Шапка"))
            {
                zoomFace = true;
                ApplyCameraFraming();
            }
            GUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();
            GUILayout.Label("<i>Работает в меню и в игре (F8 / F9)</i>");
            GUILayout.EndVertical();
        }

        private void DrawControlsColumn()
        {
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(activeTab == 0 ? "[ Внешность и Пресеты ]" : "Внешность и Пресеты", GUILayout.Height(28f)))
            {
                activeTab = 0;
            }
            if (GUILayout.Button(activeTab == 1 ? "[ Свои Текстуры ]" : "Свои Текстуры", GUILayout.Height(28f)))
            {
                activeTab = 1;
                texManager?.EnsureFoldersAndScan();
            }
            if (GUILayout.Button("Закрыть", GUILayout.Width(80f), GUILayout.Height(28f)))
            {
                IsVisible = false;
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(statusMessage))
            {
                string colorTag = statusIsError ? "red" : "lime";
                GUILayout.Label($"<color={colorTag}>{statusMessage}</color>");
            }

            GUILayout.Space(4f);
            if (activeTab == 0)
            {
                DrawAppearanceAndPresetsTab();
            }
            else
            {
                DrawCustomTexturesTab();
            }

            GUILayout.EndVertical();
        }

        private void DrawAppearanceAndPresetsTab()
        {
            if (profiles == null) return;

            slotsScrollPos = GUILayout.BeginScrollView(slotsScrollPos, GUI.skin.box, GUILayout.ExpandHeight(true));

            for (int i = 0; i < 6; i++)
            {
                ClothingSlot slot = (ClothingSlot)i;
                int count = CharacterAssetLoader.GetSlotOptionCount(slot);
                int currentIdx = profiles.Active.SlotIndices[i];
                if (count > 0) currentIdx = Mathf.Clamp(currentIdx, 0, count - 1);

                CustomTexSlot? cSlot = SkinCatalogConstants.ClothingToCustomSlot(slot);
                bool hasCustom = cSlot.HasValue && profiles.Active.HasCustomTexture(cSlot.Value);

                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                string slotTitle = SkinCatalogConstants.SlotDisplayNamesRu[i];
                string badge = hasCustom ? "<color=lime>[СВОЙ СКИН]</color>" : "<color=silver>[Стандарт]</color>";
                GUILayout.Label($"<b>{slotTitle}</b>  {badge}");
                if (hasCustom && GUILayout.Button("Снять скин", GUILayout.Width(95f)))
                {
                    profiles.Active.ClearCustomTexture(cSlot.Value);
                    profiles.ActivePresetIndex = -1;
                    SaveAndNotify();
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("<", GUILayout.Width(34f), GUILayout.Height(24f)))
                {
                    StepClothingSlot(slot, -1);
                }

                string itemLabel;
                if (hasCustom)
                {
                    string rel = profiles.Active.CustomRelativePaths[(int)cSlot.Value];
                    itemLabel = string.IsNullOrEmpty(rel) ? "Своя текстура" : Path.GetFileName(rel);
                }
                else
                {
                    string optName = CharacterAssetLoader.GetSlotOptionName(slot, currentIdx) ?? ("Вариант " + (currentIdx + 1));
                    itemLabel = count > 0 ? $"{optName} ({currentIdx + 1}/{count})" : optName;
                }

                GUILayout.Label(itemLabel, GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.Height(24f));

                if (GUILayout.Button(">", GUILayout.Width(34f), GUILayout.Height(24f)))
                {
                    StepClothingSlot(slot, 1);
                }
                GUILayout.EndHorizontal();

                if (cSlot.HasValue)
                {
                    bool curPoint = profiles.Active.IsPointFiltered(cSlot.Value);
                    bool nextPoint = GUILayout.Toggle(curPoint, "Фильтр Point (чёткие пиксели)");
                    if (nextPoint != curPoint)
                    {
                        profiles.Active.SetPointFiltered(cSlot.Value, nextPoint);
                        profiles.ActivePresetIndex = -1;
                        SaveAndNotify();
                    }
                }

                GUILayout.EndVertical();
            }

            GUILayout.EndScrollView();

            GUILayout.Space(4f);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b>Пресеты образов (1–5):</b>");

            GUILayout.BeginHorizontal();
            for (int p = 0; p < SkinCatalogConstants.PresetSlotCount; p++)
            {
                bool isCurrent = profiles.ActivePresetIndex == p;
                string btnText = isCurrent ? $"[Образ {p + 1}]" : $"Образ {p + 1}";
                if (GUILayout.Button(btnText))
                {
                    LoadPresetSlot(p);
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            for (int p = 0; p < SkinCatalogConstants.PresetSlotCount; p++)
            {
                if (GUILayout.Button($"Сохр. в {p + 1}"))
                {
                    SavePresetSlot(p);
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Случайный образ"))
            {
                RandomizeAppearance();
            }
            if (GUILayout.Button("Сбросить"))
            {
                ResetAppearance();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private void DrawCustomTexturesTab()
        {
            if (profiles == null) return;

            GUILayout.Label("<b>Выберите часть тела для своей текстуры:</b>");
            GUILayout.BeginHorizontal();
            for (int i = 0; i < 4; i++)
            {
                CustomTexSlot s = (CustomTexSlot)i;
                bool active = selectedCustomSlot == s;
                string label = active ? $"[ {CustomTabSlotLabels[i]} ]" : CustomTabSlotLabels[i];
                if (GUILayout.Button(label))
                {
                    selectedCustomSlot = s;
                }
            }
            GUILayout.EndHorizontal();

            int slotIdx = (int)selectedCustomSlot;
            bool hasCustom = profiles.Active.HasCustomTexture(selectedCustomSlot);
            string currentRel = profiles.Active.CustomRelativePaths[slotIdx];

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            string curText = hasCustom
                ? $"Текущий: <color=lime>{(string.IsNullOrEmpty(currentRel) ? "Своя текстура" : currentRel)}</color>"
                : "Текущий: <color=silver>Стандартный скин</color>";
            GUILayout.Label(curText);
            if (hasCustom && GUILayout.Button("Убрать свой скин", GUILayout.Width(140f)))
            {
                profiles.Active.ClearCustomTexture(selectedCustomSlot);
                profiles.ActivePresetIndex = -1;
                SetStatus("Свой скин для слота снят.", false);
                SaveAndNotify();
            }
            GUILayout.EndHorizontal();

            bool isPoint = profiles.Active.IsPointFiltered(selectedCustomSlot);
            bool nextPoint = GUILayout.Toggle(isPoint, "Point (пиксель-арт / без мыла)");
            if (nextPoint != isPoint)
            {
                profiles.Active.SetPointFiltered(selectedCustomSlot, nextPoint);
                profiles.ActivePresetIndex = -1;
                SaveAndNotify();
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Создать UV-шаблоны"))
            {
                if (texManager != null)
                {
                    int exported = texManager.ExportUvTemplates();
                    SetStatus($"UV-шаблоны созданы: {exported} шт. (папка Templates)", exported == 0);
                }
            }
            if (GUILayout.Button("Открыть папку Skins"))
            {
                texManager?.OpenSkinsFolder();
            }
            if (GUILayout.Button("Горячая перезагрузка"))
            {
                if (texManager != null)
                {
                    int failed = texManager.HotReloadLocalProfileChecked(profiles, out string reloadErr);
                    RefreshPreview();
                    onAppearanceChanged?.Invoke();
                    if (failed > 0)
                    {
                        SetStatus($"Перезагрузка с ошибками ({failed}): {reloadErr}", true);
                    }
                    else
                    {
                        SetStatus("Текстуры и профиль перезагружены с диска.", false);
                    }
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.BeginHorizontal();
            int availCount = (texManager != null && texManager.AvailableFiles != null) ? texManager.AvailableFiles.Count : 0;
            GUILayout.Label($"<b>Файлы в папке Skins ({availCount}):</b>");
            if (GUILayout.Button("Обновить список", GUILayout.Width(130f)))
            {
                texManager?.EnsureFoldersAndScan();
            }
            GUILayout.EndHorizontal();

            filesScrollPos = GUILayout.BeginScrollView(filesScrollPos, GUI.skin.box, GUILayout.Height(150f));
            if (texManager != null && texManager.AvailableFiles != null && texManager.AvailableFiles.Count > 0)
            {
                IList<string> files = texManager.AvailableFiles;
                for (int i = 0; i < files.Count; i++)
                {
                    string rel = files[i];
                    bool selected = hasCustom && string.Equals(rel, currentRel, StringComparison.OrdinalIgnoreCase);
                    string btnText = selected ? $"[ВЫБРАН] {rel}" : rel;
                    if (GUILayout.Button(btnText))
                    {
                        ApplyCustomTextureFile(rel, selectedCustomSlot);
                    }
                }
            }
            else
            {
                GUILayout.Label("<i>Нет .png/.jpg файлов в Skins/. Положите файл в папку или импортируйте ниже.</i>");
            }
            GUILayout.EndScrollView();

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>Быстрый импорт (Desktop / Загрузки / Картинки):</b>");
            if (GUILayout.Button("Сканировать", GUILayout.Width(105f)))
            {
                if (texManager != null)
                {
                    quickImportScannedOnce = true;
                    quickImportCandidates = texManager.ScanQuickImportSources() ?? new List<QuickImportCandidate>();
                    SetStatus($"Найдено изображений: {quickImportCandidates.Count}", false);
                }
            }
            GUILayout.EndHorizontal();

            quickImportScrollPos = GUILayout.BeginScrollView(quickImportScrollPos, GUI.skin.box, GUILayout.Height(86f));
            if (quickImportCandidates != null && quickImportCandidates.Count > 0)
            {
                for (int i = 0; i < quickImportCandidates.Count; i++)
                {
                    QuickImportCandidate cand = quickImportCandidates[i];
                    if (cand == null) continue;
                    string label = string.IsNullOrEmpty(cand.DisplayName) ? Path.GetFileName(cand.FullPath) : cand.DisplayName;
                    if (GUILayout.Button("Импорт: " + label))
                    {
                        ImportAndApplyExternal(cand.FullPath, selectedCustomSlot);
                    }
                }
            }
            else
            {
                GUILayout.Label("<i>Нет найденных .png/.jpg или нажмите «Сканировать».</i>");
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Путь:", GUILayout.Width(38f));
            manualImportPath = GUILayout.TextField(manualImportPath ?? string.Empty);
            if (GUILayout.Button("Импорт", GUILayout.Width(76f)))
            {
                ImportAndApplyExternal(manualImportPath, selectedCustomSlot);
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private void StepClothingSlot(ClothingSlot slot, int delta)
        {
            if (profiles == null) return;
            int count = CharacterAssetLoader.GetSlotOptionCount(slot);
            if (count <= 0) return;

            int idx = (int)slot;
            int cur = Mathf.Clamp(profiles.Active.SlotIndices[idx], 0, count - 1);
            int next = (cur + delta + count) % count;

            CustomTexSlot? cSlot = SkinCatalogConstants.ClothingToCustomSlot(slot);
            if (cSlot.HasValue && profiles.Active.HasCustomTexture(cSlot.Value))
            {
                profiles.Active.ClearCustomTexture(cSlot.Value);
            }

            profiles.Active.SlotIndices[idx] = (byte)next;
            profiles.ActivePresetIndex = -1;
            SaveAndNotify();
        }

        private void LoadPresetSlot(int presetIndex)
        {
            if (profiles == null || presetIndex < 0 || presetIndex >= profiles.Presets.Length) return;

            profiles.Active.CopyFrom(profiles.Presets[presetIndex]);
            profiles.ActivePresetIndex = presetIndex;

            if (texManager != null)
            {
                for (int i = 0; i < 4; i++)
                {
                    CustomTexSlot cSlot = (CustomTexSlot)i;
                    string rel = profiles.Active.CustomRelativePaths[i];
                    if (string.IsNullOrEmpty(rel)) continue;

                    uint crc;
                    byte[] bytes;
                    Texture2D preview;
                    string err;
                    if (texManager.TryPrepareLocalTexture(rel, cSlot, out crc, out bytes, out preview, out err))
                    {
                        profiles.Active.SetCustomTexture(cSlot, rel, crc, bytes != null ? bytes.Length : 0);
                    }
                    else
                    {
                        profiles.Active.ClearCustomTexture(cSlot);
                    }
                }
            }

            SetStatus($"Загружен «Образ {presetIndex + 1}».", false);
            SaveAndNotify();
        }

        private void SavePresetSlot(int presetIndex)
        {
            if (profiles == null || presetIndex < 0 || presetIndex >= profiles.Presets.Length) return;
            profiles.Presets[presetIndex].CopyFrom(profiles.Active);
            profiles.ActivePresetIndex = presetIndex;
            if (texManager != null)
            {
                profiles.Save(texManager.ProfilesConfigPath);
            }
            SetStatus($"Текущий вид сохранён в «Образ {presetIndex + 1}».", false);
        }

        private void RandomizeAppearance()
        {
            if (profiles == null) return;
            for (int i = 0; i < 6; i++)
            {
                int count = CharacterAssetLoader.GetSlotOptionCount((ClothingSlot)i);
                if (count > 0)
                {
                    profiles.Active.SlotIndices[i] = (byte)rng.Next(0, count);
                }
            }
            for (int i = 0; i < 4; i++)
            {
                profiles.Active.ClearCustomTexture((CustomTexSlot)i);
            }
            profiles.ActivePresetIndex = -1;
            SetStatus("Сгенерирован случайный образ.", false);
            SaveAndNotify();
        }

        private void ResetAppearance()
        {
            if (profiles == null) return;
            profiles.Active.ResetToDefaults();
            profiles.ActivePresetIndex = -1;
            SetStatus("Внешность сброшена к стандартной.", false);
            SaveAndNotify();
        }

        private void ApplyCustomTextureFile(string relPath, CustomTexSlot slot)
        {
            if (profiles == null || texManager == null) return;

            uint crc;
            byte[] encodedBytes;
            Texture2D previewTex;
            string error;
            if (!texManager.TryPrepareLocalTexture(relPath, slot, out crc, out encodedBytes, out previewTex, out error))
            {
                SetStatus(string.IsNullOrEmpty(error) ? "Не удалось применить текстуру." : error, true);
                return;
            }

            profiles.Active.SetCustomTexture(slot, relPath, crc, encodedBytes != null ? encodedBytes.Length : 0);
            profiles.ActivePresetIndex = -1;

            // Обувь на модели скрыта при индексе 0 — автоматически переключаем на 1, чтобы скин сразу был виден
            if (slot == CustomTexSlot.Shoes && profiles.Active.SlotIndices[(int)ClothingSlot.Shoes] == 0)
            {
                profiles.Active.SlotIndices[(int)ClothingSlot.Shoes] = 1;
            }

            SetStatus($"Применено ({CustomTabSlotLabels[(int)slot]}): {Path.GetFileName(relPath)}", false);
            SaveAndNotify();
        }

        private void ImportAndApplyExternal(string fullPath, CustomTexSlot slot)
        {
            if (texManager == null) return;

            string importedRel;
            string error;
            if (!texManager.TryImportExternalFile(fullPath, slot, out importedRel, out error))
            {
                SetStatus(string.IsNullOrEmpty(error) ? "Ошибка импорта файла." : error, true);
                return;
            }

            ApplyCustomTextureFile(importedRel, slot);
        }

        private void SaveAndNotify()
        {
            if (profiles != null && texManager != null)
            {
                profiles.Save(texManager.ProfilesConfigPath);
            }
            RefreshPreview();
            onAppearanceChanged?.Invoke();
        }

        private void SetStatus(string msg, bool isError)
        {
            statusMessage = msg ?? string.Empty;
            statusIsError = isError;
        }

        private void HandlePreviewMouseDrag(Rect rect)
        {
            Event evt = Event.current;
            if (evt == null) return;

            if (evt.type == EventType.MouseDown && evt.button == 0 && rect.Contains(evt.mousePosition))
            {
                isDraggingPreview = true;
                evt.Use();
            }
            else if (evt.type == EventType.MouseDrag && isDraggingPreview)
            {
                previewYaw = WrapAngle360(previewYaw - evt.delta.x * 0.85f);
                evt.Use();
            }
            else if ((evt.type == EventType.MouseUp || evt.type == EventType.Ignore) && isDraggingPreview)
            {
                isDraggingPreview = false;
            }
        }

        private void EnsurePreviewRig()
        {
            if (!CharacterAssetLoader.IsLoaded)
            {
                CharacterAssetLoader.LoadAssets(log);
            }

            if (previewRoot == null)
            {
                previewRoot = new GameObject("OSC_WardrobePreviewRig");
                UnityEngine.Object.DontDestroyOnLoad(previewRoot);
                previewRoot.transform.position = RigPosition;
            }

            if (previewTexture == null)
            {
                previewTexture = new RenderTexture(PreviewWidth, PreviewHeight, 16, RenderTextureFormat.ARGB32);
                previewTexture.name = "OSC_WardrobePreviewRT";
                previewTexture.Create();
            }

            if (previewCamera == null)
            {
                GameObject camGo = new GameObject("PreviewCamera");
                camGo.transform.SetParent(previewRoot.transform, false);
                previewCamera = camGo.AddComponent<Camera>();
                previewCamera.targetTexture = previewTexture;
                previewCamera.clearFlags = CameraClearFlags.SolidColor;
                previewCamera.backgroundColor = new Color(0.11f, 0.13f, 0.17f, 1f);
                previewCamera.fieldOfView = 30f;
                previewCamera.nearClipPlane = 0.08f;
                previewCamera.farClipPlane = 15f;
                previewCamera.cullingMask = ~(1 << 14);
                previewCamera.depth = -50f;

                CreateStudioLight("KeyLight", new Vector3(1.1f, 2.0f, 2.5f), new Color(1f, 0.97f, 0.93f), 1.75f);
                CreateStudioLight("FillLight", new Vector3(-1.3f, 1.3f, 1.8f), new Color(0.75f, 0.85f, 1f), 0.9f);
                CreateStudioLight("RimLight", new Vector3(0f, 1.8f, -2.2f), new Color(0.85f, 0.9f, 1f), 0.75f);
            }

            if (previewModel == null && CharacterAssetLoader.IsLoaded)
            {
                previewModel = CharacterAssetLoader.InstantiateCustomModel("OSC_WardrobePreviewChar", previewRoot.transform, log);
                if (previewModel != null)
                {
                    previewModel.transform.localPosition = Vector3.zero;
                    previewModel.transform.localEulerAngles = new Vector3(0f, previewYaw, 0f);

                    Transform innerCam = previewModel.transform.Find("char/Camera");
                    if (innerCam != null) innerCam.gameObject.SetActive(false);

                    AudioListener[] listeners = previewModel.GetComponentsInChildren<AudioListener>(true);
                    for (int i = 0; i < listeners.Length; i++)
                    {
                        if (listeners[i] != null) listeners[i].enabled = false;
                    }

                    Collider[] colliders = previewModel.GetComponentsInChildren<Collider>(true);
                    for (int i = 0; i < colliders.Length; i++)
                    {
                        if (colliders[i] != null) UnityEngine.Object.Destroy(colliders[i]);
                    }

                    Transform bodyTf = previewModel.transform.Find("char/bodymesh");
                    if (bodyTf != null)
                    {
                        previewBodyMesh = bodyTf.GetComponent<SkinnedMeshRenderer>();
                        if (previewBodyMesh != null)
                        {
                            previewBodyMesh.updateWhenOffscreen = true;
                        }
                    }

                    previewHeadTransform = previewModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/headPivot/HeadRotationPivot/head");
                    if (previewHeadTransform == null)
                    {
                        previewHeadTransform = previewModel.transform.Find("char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/headPivot");
                    }

                    previewNeedsFraming = true;
                }
            }
        }

        private void CreateStudioLight(string name, Vector3 localPos, Color color, float intensity)
        {
            GameObject lightGo = new GameObject(name);
            lightGo.transform.SetParent(previewRoot.transform, false);
            lightGo.transform.localPosition = localPos;
            Light l = lightGo.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = color;
            l.intensity = intensity;
            l.range = 12f;
        }

        private void SetPreviewRigActive(bool active)
        {
            if (previewRoot != null) previewRoot.SetActive(active);
            if (previewModel != null) previewModel.SetActive(active);
            if (previewCamera != null) previewCamera.enabled = active;
        }

        private void ApplyCameraFraming()
        {
            if (previewCamera == null || previewRoot == null) return;

            if (zoomFace)
            {
                Vector3 headCenter = previewHeadTransform != null
                    ? previewHeadTransform.position + new Vector3(0f, 0.05f, 0f)
                    : previewRoot.transform.position + new Vector3(0f, 1.52f, 0f);
                Vector3 target = new Vector3(previewRoot.transform.position.x, headCenter.y, previewRoot.transform.position.z);
                previewCamera.transform.position = target + new Vector3(0f, 0.02f, 1.05f);
                previewCamera.transform.LookAt(target);
                return;
            }

            float centerY = previewRoot.transform.position.y + 0.9f;
            float dist = 3.8f;

            if (previewBodyMesh != null)
            {
                Bounds b = previewBodyMesh.bounds;
                if (b.size.y > 0.2f)
                {
                    centerY = b.center.y;
                    float halfHeight = Mathf.Max(b.extents.y, 0.72f);
                    dist = (halfHeight * 1.18f) / Mathf.Tan(previewCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                    dist = Mathf.Clamp(dist, 2.2f, 6.0f);
                }
            }

            Vector3 fullTarget = new Vector3(previewRoot.transform.position.x, centerY, previewRoot.transform.position.z);
            previewCamera.transform.position = fullTarget + new Vector3(0f, 0.04f, dist);
            previewCamera.transform.LookAt(fullTarget);
        }

        private static float WrapAngle360(float angle)
        {
            angle %= 360f;
            if (angle < 0f) angle += 360f;
            return angle;
        }
    }
}
