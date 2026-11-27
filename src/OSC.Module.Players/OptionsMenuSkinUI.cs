using System;
using System.IO;
using UnityEngine;

namespace OSC.Module.Players
{
    public sealed class OptionsMenuSkinUI
    {
        private struct SlotRow
        {
            public ClothingSlot Slot;
            public Collider ButtonLeft;
            public Collider ButtonRight;
            public TextMesh FieldText;
            public TextMesh FieldShadow;
            public int LayerMask;
        }

        private const int GuiLayerBit = 16384; // 1 << 14

        private readonly SkinPresetCollection profiles;
        private readonly CustomSkinTextureManager texManager;
        private readonly Action onAppearanceChanged;
        private readonly Action<string> log;

        private GameObject menuRoot;
        private GameObject settingTab;
        private Transform guiTextLabel;
        private Collider buttonCollider;
        private GameObject guiCharacter;
        private GameObject skinRenderRoot;
        private GameObject localPlayerModel;
        private GameObject localModelCameraGo;
        private Camera localModelCamera;
        private GameObject[] otherMenus;
        private SlotRow[] slotRows;

        private bool mouseOver;
        private bool uiVisible;
        private float nextInitCheckTime;
        private Camera[] cachedCameras = new Camera[16];

        public OptionsMenuSkinUI(
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

        public void TryInitializeInScene()
        {
            if (menuRoot != null && settingTab != null) return;
            if (menuRoot == null) Cleanup();

            if (!CharacterAssetLoader.IsLoaded)
            {
                CharacterAssetLoader.LoadAssets(log);
            }
            if (CharacterAssetLoader.SettingsCharPrefab == null || CharacterAssetLoader.LocalPlayerSkinRenderPrefab == null)
            {
                return;
            }

            GameObject systemsGo = GameObject.Find("Systems");
            if (systemsGo == null) return;

            Transform optionsMenuTf = systemsGo.transform.Find("OptionsMenu");
            if (optionsMenuTf == null) return;

            Transform menuTf = optionsMenuTf.Find("Menu");
            if (menuTf == null) return;

            if (menuTf.Find("Btn_PlayerCustomization") != null) return;

            Transform btnGraphicsTf = menuTf.Find("Btn_Graphics");
            if (btnGraphicsTf == null) return;

            menuRoot = menuTf.gameObject;

            // Аккуратно сдвигаем элементы меню, чтобы разместить кнопку SKIN CONFIG (не трогая Btn_ConfirmQuit!)
            SetLocalTransform(menuTf.Find("Table 5"), new Vector3(0f, -1.1f, 0.01f), new Vector3(1f, 1.3f, 1f));
            SetLocalPosition(menuTf.Find("Table 3"), new Vector3(0f, 1.55f, 0.01f));
            SetLocalPosition(menuTf.Find("Header 4"), new Vector3(0f, 2.55f, 0.02f));
            SetLocalTransform(menuTf.Find("BoxBG"), new Vector3(0f, -0.7f, 1f), new Vector3(6.5f, 7.75f, 1f));
            SetLocalPosition(menuTf.Find("Btn_Resume"), new Vector3(2.5f, 1.5f, -0.1f));

            settingTab = UnityEngine.Object.Instantiate(btnGraphicsTf.gameObject);
            settingTab.transform.SetParent(menuTf, false);
            settingTab.name = "Btn_PlayerCustomization";
            settingTab.transform.localPosition = new Vector3(2.5f, 0f, -0.1f);
            settingTab.transform.localRotation = btnGraphicsTf.localRotation;
            settingTab.transform.localScale = btnGraphicsTf.localScale;

            Transform btnChild = settingTab.transform.Find("Button");
            if (btnChild != null)
            {
                PlayMakerFSM btnFsm = btnChild.GetComponent<PlayMakerFSM>();
                if (btnFsm != null) UnityEngine.Object.Destroy(btnFsm);
                buttonCollider = btnChild.GetComponent<Collider>();
            }

            guiTextLabel = settingTab.transform.Find("GUITextLabel");
            if (guiTextLabel != null)
            {
                TextMesh mainLabel = guiTextLabel.GetComponent<TextMesh>();
                if (mainLabel != null) mainLabel.text = "SKIN CONFIG";
                if (guiTextLabel.childCount > 0)
                {
                    TextMesh shadowLabel = guiTextLabel.GetChild(0).GetComponent<TextMesh>();
                    if (shadowLabel != null) shadowLabel.text = "SKIN CONFIG";
                }
            }

            otherMenus = new GameObject[4];
            otherMenus[0] = FindChildGo(optionsMenuTf, "DEBUG");
            otherMenus[1] = FindChildGo(optionsMenuTf, "Graphics");
            otherMenus[2] = FindChildGo(optionsMenuTf, "VehicleControls");
            otherMenus[3] = FindChildGo(optionsMenuTf, "PlayerControls");

            skinRenderRoot = UnityEngine.Object.Instantiate(CharacterAssetLoader.LocalPlayerSkinRenderPrefab);
            skinRenderRoot.name = "OSC_LocalPlayerSkinRender";
            skinRenderRoot.transform.position = Vector3.up * -10f;
            DisableAudioListeners(skinRenderRoot);
            DestroyColliders(skinRenderRoot);
            skinRenderRoot.SetActive(false);

            localPlayerModel = CharacterAssetLoader.InstantiateCustomModel("localPlayerModel", skinRenderRoot.transform, log);
            if (localPlayerModel != null)
            {
                localPlayerModel.transform.localPosition = Vector3.zero;
                localPlayerModel.transform.localEulerAngles = Vector3.zero;
                DisableAudioListeners(localPlayerModel);
                DestroyColliders(localPlayerModel);

                Transform camTf = localPlayerModel.transform.Find("char/Camera");
                if (camTf != null)
                {
                    localModelCameraGo = camTf.gameObject;
                    localModelCamera = localModelCameraGo.GetComponent<Camera>();
                    if (localModelCameraGo.CompareTag("MainCamera"))
                    {
                        localModelCameraGo.tag = "Untagged";
                    }
                    localModelCameraGo.SetActive(false);
                }
                localPlayerModel.SetActive(false);
            }

            guiCharacter = UnityEngine.Object.Instantiate(CharacterAssetLoader.SettingsCharPrefab);
            guiCharacter.transform.SetParent(optionsMenuTf, false);
            guiCharacter.name = "PlayerCustomization";
            guiCharacter.transform.localPosition = new Vector3(4f, -0.1f, 0f);
            guiCharacter.transform.localEulerAngles = new Vector3(270f, 0f, 0f);
            guiCharacter.transform.localScale = new Vector3(1.5f, 1.5f, 1.5f);
            guiCharacter.SetActive(false);

            Transform buttonsRoot = guiCharacter.transform.Find("Page/Buttons");
            Transform fieldsRoot = guiCharacter.transform.Find("Page/FieldString");
            slotRows = new SlotRow[6];
            for (int i = 0; i < 6; i++)
            {
                var row = new SlotRow
                {
                    Slot = (ClothingSlot)i,
                    LayerMask = GuiLayerBit
                };

                if (buttonsRoot != null && i < buttonsRoot.childCount)
                {
                    Transform btnRow = buttonsRoot.GetChild(i);
                    if (btnRow != null && btnRow.childCount >= 2)
                    {
                        row.ButtonRight = btnRow.GetChild(0).GetComponent<Collider>();
                        row.ButtonLeft = btnRow.GetChild(1).GetComponent<Collider>();
                        if (row.ButtonLeft != null)
                        {
                            row.LayerMask = 1 << row.ButtonLeft.gameObject.layer;
                        }
                    }
                }

                if (fieldsRoot != null && i < fieldsRoot.childCount)
                {
                    Transform fieldRow = fieldsRoot.GetChild(i);
                    if (fieldRow != null)
                    {
                        row.FieldText = fieldRow.GetComponent<TextMesh>();
                        if (fieldRow.childCount > 0)
                        {
                            row.FieldShadow = fieldRow.GetChild(0).GetComponent<TextMesh>();
                        }
                    }
                }

                slotRows[i] = row;
            }

            RefreshVisualsFromActive();
            log?.Invoke("[SkinUI] Панель SKIN CONFIG интегрирована в меню паузы.");
        }

        public void TickUpdate()
        {
            if (menuRoot == null || settingTab == null || guiCharacter == null)
            {
                if (Time.unscaledTime < nextInitCheckTime) return;
                nextInitCheckTime = Time.unscaledTime + 0.5f;

                GameObject systemsGo = GameObject.Find("Systems");
                Transform optionsMenuTf = systemsGo != null ? systemsGo.transform.Find("OptionsMenu") : null;
                Transform menuTf = optionsMenuTf != null ? optionsMenuTf.Find("Menu") : null;
                if (menuTf != null && menuTf.gameObject.activeInHierarchy && HasValidNonZeroScale(menuTf))
                {
                    TryInitializeInScene();
                }

                if (menuRoot == null || settingTab == null || guiCharacter == null) return;
            }

            if (!menuRoot.activeInHierarchy || !settingTab.activeInHierarchy)
            {
                if (uiVisible) SetUiVisible(false);
                if (mouseOver)
                {
                    mouseOver = false;
                    if (guiTextLabel != null) guiTextLabel.localScale = Vector3.one;
                }
                return;
            }

            if (uiVisible && IsAnyOtherMenuActive())
            {
                SetUiVisible(false);
                return;
            }

            Camera rayCam = FindMenuCamera();
            if (rayCam == null) return;

            Ray ray = rayCam.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;

            bool hoveringTab = buttonCollider != null &&
                               Physics.Raycast(ray, out hit, 150f, GuiLayerBit) &&
                               hit.collider == buttonCollider;

            if (hoveringTab != mouseOver)
            {
                mouseOver = hoveringTab;
                if (guiTextLabel != null)
                {
                    guiTextLabel.localScale = Vector3.one * (hoveringTab ? 0.95f : 1f);
                }
            }

            if (!Input.GetMouseButtonDown(0)) return;

            if (hoveringTab)
            {
                if (!uiVisible)
                {
                    HideOtherMenus();
                    SetUiVisible(true);
                    RefreshVisualsFromActive();
                }
                else
                {
                    SetUiVisible(false);
                }
                return;
            }

            if (!uiVisible || !guiCharacter.activeInHierarchy || slotRows == null) return;

            int lastMask = int.MinValue;
            bool hasHit = false;
            RaycastHit rowHit = default(RaycastHit);

            for (int i = 0; i < slotRows.Length; i++)
            {
                SlotRow row = slotRows[i];
                if (row.ButtonLeft == null && row.ButtonRight == null) continue;

                if (row.LayerMask != lastMask)
                {
                    lastMask = row.LayerMask;
                    hasHit = Physics.Raycast(ray, out rowHit, 150f, lastMask);
                }
                if (!hasHit) continue;

                if (row.ButtonLeft != null && rowHit.collider == row.ButtonLeft)
                {
                    StepSlot(row.Slot, -1);
                    return;
                }
                if (row.ButtonRight != null && rowHit.collider == row.ButtonRight)
                {
                    StepSlot(row.Slot, 1);
                    return;
                }
            }
        }

        public void RefreshVisualsFromActive()
        {
            if (profiles == null) return;

            if (slotRows != null)
            {
                for (int i = 0; i < slotRows.Length; i++)
                {
                    SlotRow row = slotRows[i];
                    int idx = profiles.Active.SlotIndices[(int)row.Slot];
                    string text = CharacterAssetLoader.GetSlotOptionName(row.Slot, idx) ?? ("INDEX " + idx);

                    CustomTexSlot? cSlot = SkinCatalogConstants.ClothingToCustomSlot(row.Slot);
                    if (cSlot.HasValue && profiles.Active.HasCustomTexture(cSlot.Value))
                    {
                        string rel = profiles.Active.CustomRelativePaths[(int)cSlot.Value];
                        string baseName = string.IsNullOrEmpty(rel) ? "SKIN" : Path.GetFileNameWithoutExtension(rel);
                        if (baseName.Length > 14) baseName = baseName.Substring(0, 14);
                        text = "CUSTOM: " + baseName;
                    }

                    string upper = text.ToUpper();
                    if (row.FieldText != null) row.FieldText.text = upper;
                    if (row.FieldShadow != null) row.FieldShadow.text = upper;
                }
            }

            if (localPlayerModel != null)
            {
                CharacterAssetLoader.ApplyAppearance(localPlayerModel, profiles.Active, texManager, false, log);
            }
        }

        public void Cleanup()
        {
            uiVisible = false;
            mouseOver = false;
            nextInitCheckTime = 0f;

            if (settingTab != null) UnityEngine.Object.Destroy(settingTab);
            if (guiCharacter != null) UnityEngine.Object.Destroy(guiCharacter);
            if (localPlayerModel != null) UnityEngine.Object.Destroy(localPlayerModel);
            if (skinRenderRoot != null) UnityEngine.Object.Destroy(skinRenderRoot);

            menuRoot = null;
            settingTab = null;
            guiTextLabel = null;
            buttonCollider = null;
            guiCharacter = null;
            skinRenderRoot = null;
            localPlayerModel = null;
            localModelCameraGo = null;
            localModelCamera = null;
            otherMenus = null;
            slotRows = null;
        }

        private void StepSlot(ClothingSlot slot, int delta)
        {
            if (profiles == null) return;

            int count = CharacterAssetLoader.GetSlotOptionCount(slot);
            if (count <= 0) return;

            int slotIdx = (int)slot;
            int cur = Mathf.Clamp(profiles.Active.SlotIndices[slotIdx], 0, count - 1);
            int next = (cur + delta + count) % count;

            CustomTexSlot? cSlot = SkinCatalogConstants.ClothingToCustomSlot(slot);
            if (cSlot.HasValue && profiles.Active.HasCustomTexture(cSlot.Value))
            {
                profiles.Active.ClearCustomTexture(cSlot.Value);
            }

            profiles.Active.SlotIndices[slotIdx] = (byte)next;
            profiles.ActivePresetIndex = -1;

            if (texManager != null)
            {
                profiles.Save(texManager.ProfilesConfigPath);
            }
            RefreshVisualsFromActive();
            onAppearanceChanged?.Invoke();
        }

        private void SetUiVisible(bool visible)
        {
            uiVisible = visible;
            if (guiCharacter != null) guiCharacter.SetActive(visible);
            if (skinRenderRoot != null) skinRenderRoot.SetActive(visible);
            if (localPlayerModel != null) localPlayerModel.SetActive(visible);
            if (localModelCameraGo != null) localModelCameraGo.SetActive(visible);
        }

        private bool IsAnyOtherMenuActive()
        {
            if (otherMenus == null) return false;
            for (int i = 0; i < otherMenus.Length; i++)
            {
                if (otherMenus[i] != null && otherMenus[i].activeSelf) return true;
            }
            return false;
        }

        private void HideOtherMenus()
        {
            if (otherMenus == null) return;
            for (int i = 0; i < otherMenus.Length; i++)
            {
                if (otherMenus[i] != null) otherMenus[i].SetActive(false);
            }
        }

        private Camera FindMenuCamera()
        {
            int count = Camera.allCamerasCount;
            if (count > 0)
            {
                if (cachedCameras.Length < count)
                {
                    cachedCameras = new Camera[count];
                }
                int filled = Camera.GetAllCameras(cachedCameras);

                Camera guiCamFallback = null;
                for (int i = 0; i < filled; i++)
                {
                    Camera cam = cachedCameras[i];
                    if (cam == null || !cam.enabled || !cam.gameObject.activeInHierarchy) continue;
                    if (cam == localModelCamera || cam.targetTexture != null) continue;
                    if ((cam.cullingMask & GuiLayerBit) == 0) continue;

                    if (cam != Camera.main)
                    {
                        return cam;
                    }
                    guiCamFallback = cam;
                }
                if (guiCamFallback != null) return guiCamFallback;
            }

            Camera mainCam = Camera.main;
            if (mainCam != null && mainCam.enabled && mainCam.gameObject.activeInHierarchy && mainCam != localModelCamera)
            {
                return mainCam;
            }
            return null;
        }

        private static GameObject FindChildGo(Transform parent, string name)
        {
            if (parent == null) return null;
            Transform t = parent.Find(name);
            return t != null ? t.gameObject : null;
        }

        private static void SetLocalPosition(Transform tf, Vector3 pos)
        {
            if (tf != null) tf.localPosition = pos;
        }

        private static void SetLocalTransform(Transform tf, Vector3 pos, Vector3 scale)
        {
            if (tf == null) return;
            tf.localPosition = pos;
            tf.localScale = scale;
        }

        private static void DisableAudioListeners(GameObject root)
        {
            if (root == null) return;
            AudioListener[] listeners = root.GetComponentsInChildren<AudioListener>(true);
            for (int i = 0; i < listeners.Length; i++)
            {
                if (listeners[i] != null) listeners[i].enabled = false;
            }
        }

        private static void DestroyColliders(GameObject root)
        {
            if (root == null) return;
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null) UnityEngine.Object.Destroy(colliders[i]);
            }
        }

        private static bool HasValidNonZeroScale(Transform tf)
        {
            if (tf == null) return false;
            Vector3 ls = tf.lossyScale;
            return !float.IsNaN(ls.x) && !float.IsNaN(ls.y) && !float.IsNaN(ls.z) &&
                   Mathf.Abs(ls.x) > 0.001f && Mathf.Abs(ls.y) > 0.001f && Mathf.Abs(ls.z) > 0.001f;
        }
    }
}
