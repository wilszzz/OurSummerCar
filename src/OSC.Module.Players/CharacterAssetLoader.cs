using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace OSC.Module.Players
{
    public static class CharacterAssetLoader
    {
        private const string GlassesPath = "char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/headPivot/HeadRotationPivot/head/glasses";
        private const string HeadgearPath = "char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/headPivot/HeadRotationPivot/head/head_end";
        private const string BodyMeshPath = "char/bodymesh";
        private const string ShoeLeftPath = "char/skeleton/thig_left/knee_left/ankle_left/shoeLeft";
        private const string ShoeRightPath = "char/skeleton/thig_right/knee_right/ankle_right/shoeRight";

        private static readonly PlayerAppearanceState DefaultState = new PlayerAppearanceState();

        private static AssetBundle clothesBundle;
        private static GameObject charPrefab;
        private static GameObject settingsCharPrefab;
        private static GameObject localPlayerSkinRenderPrefab;

        private static Texture2D[] faces;
        private static Texture2D[] shirts;
        private static Texture2D[] pants;
        private static Texture2D[] shoes;

        private static int glassesCount;
        private static int headgearCount;
        private static string[] glassesNames = new string[0];
        private static string[] headgearNames = new string[0];

        public static bool IsLoaded => charPrefab != null;
        public static bool HasAttemptedLoad { get; private set; }
        public static string LoadedPath { get; private set; } = string.Empty;
        public static string StatusMessage { get; private set; } = "Не загружен";

        public static GameObject SettingsCharPrefab => settingsCharPrefab;
        public static GameObject LocalPlayerSkinRenderPrefab => localPlayerSkinRenderPrefab;

        public static Texture2D[] Faces => faces;
        public static Texture2D[] Shirts => shirts;
        public static Texture2D[] Pants => pants;
        public static Texture2D[] Shoes => shoes;

        public static int GlassesCount => glassesCount;
        public static int HeadgearCount => headgearCount;

        public static bool LoadAssets(Action<string> log = null, bool force = false)
        {
            if (charPrefab != null) return true;
            if (HasAttemptedLoad && !force) return false;
            HasAttemptedLoad = true;

            try
            {
                string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string executingDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? string.Empty;

                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(executingDir))
                {
                    candidates.Add(Path.Combine(executingDir, "clothes.unity3d"));
                    candidates.Add(Path.Combine(executingDir, "../clothes.unity3d"));
                }

                candidates.Add(Path.Combine(myDocs, "MySummerCar/Mods/Assets/OurSummerCar/clothes.unity3d"));
                candidates.Add(Path.Combine(myDocs, "MySummerCar/Mods/Assets/clothes.unity3d"));
                candidates.Add(Path.Combine(Application.dataPath, "../Mods/Assets/OurSummerCar/clothes.unity3d"));
#if DEBUG
                candidates.Add(@"c:\me\Antigravity projects\OurSummerCar\Assets\clothes.unity3d");
#endif

                string bundlePath = null;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (File.Exists(candidates[i]))
                    {
                        bundlePath = candidates[i];
                        break;
                    }
                }

                if (string.IsNullOrEmpty(bundlePath))
                {
                    StatusMessage = "clothes.unity3d не найден";
                    log?.Invoke("[Игроки] clothes.unity3d не найден в Mods/Assets/OurSummerCar/");
                    return false;
                }

                byte[] bundleBytes = File.ReadAllBytes(bundlePath);
                if (bundleBytes == null || bundleBytes.Length == 0)
                {
                    StatusMessage = "clothes.unity3d пуст";
                    return false;
                }

                clothesBundle = AssetBundle.CreateFromMemoryImmediate(bundleBytes);
                if (clothesBundle == null)
                {
                    StatusMessage = "Ошибка десериализации AssetBundle";
                    return false;
                }

                GameObject[] allGoCache = null;
                charPrefab = LoadPrefabAsset(clothesBundle, "char.prefab", "char", ref allGoCache);
                if (charPrefab == null)
                {
                    StatusMessage = "char.prefab не найден";
                    return false;
                }

                settingsCharPrefab = LoadPrefabAsset(clothesBundle, "Settings_Char.prefab", "Settings_Char", ref allGoCache);
                localPlayerSkinRenderPrefab = LoadPrefabAsset(clothesBundle, "LocalPlayerSkinRender.prefab", "LocalPlayerSkinRender", ref allGoCache);

                CacheHeadAccessoryMetadata(charPrefab);
                LoadCustomizationTextures();

                LoadedPath = bundlePath;
                StatusMessage = "OK";
                log?.Invoke($"[Игроки] 3D-модель загружена: {Path.GetFileName(bundlePath)}");
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                log?.Invoke($"[CharacterAssetLoader Error] {ex.Message}");
                return false;
            }
        }

        public static int GetSlotOptionCount(ClothingSlot slot)
        {
            switch (slot)
            {
                case ClothingSlot.Glasses:
                    return glassesCount;
                case ClothingSlot.Headgear:
                    return headgearCount;
                case ClothingSlot.Face:
                    return faces != null ? faces.Length : SkinCatalogConstants.NumBuiltInFaces;
                case ClothingSlot.Shirt:
                    return shirts != null ? shirts.Length : SkinCatalogConstants.NumBuiltInShirts;
                case ClothingSlot.Pants:
                    return pants != null ? pants.Length : SkinCatalogConstants.NumBuiltInPants;
                case ClothingSlot.Shoes:
                    return (shoes != null ? shoes.Length : SkinCatalogConstants.NumBuiltInShoes) + 1;
                default:
                    return 0;
            }
        }

        public static string GetSlotOptionName(ClothingSlot slot, int index)
        {
            switch (slot)
            {
                case ClothingSlot.Glasses:
                    if (glassesNames != null && index >= 0 && index < glassesNames.Length)
                    {
                        return glassesNames[index];
                    }
                    return $"INDEX {index}";

                case ClothingSlot.Headgear:
                    if (headgearNames != null && index >= 0 && index < headgearNames.Length)
                    {
                        return headgearNames[index];
                    }
                    return $"INDEX {index}";

                case ClothingSlot.Face:
                    return $"INDEX {index}";

                case ClothingSlot.Shirt:
                    if (index >= 0 && index < SkinCatalogConstants.NamesShirts.Length)
                    {
                        return SkinCatalogConstants.NamesShirts[index];
                    }
                    return $"INDEX {index}";

                case ClothingSlot.Pants:
                    if (index >= 0 && index < SkinCatalogConstants.NamesPants.Length)
                    {
                        return SkinCatalogConstants.NamesPants[index];
                    }
                    return $"INDEX {index}";

                case ClothingSlot.Shoes:
                    return (index <= 0) ? "None" : $"INDEX {index}";

                default:
                    return $"INDEX {index}";
            }
        }

        public static GameObject InstantiateCharacter(int playerId, Transform parent, Action<string> log = null)
        {
            if (charPrefab == null && !LoadAssets(log)) return null;

            try
            {
                GameObject characterObj = UnityEngine.Object.Instantiate(charPrefab);
                characterObj.name = (playerId == 0) ? "Character_Host" : $"Character_Client_{playerId}";
                if (parent != null)
                {
                    characterObj.transform.SetParent(parent, false);
                    characterObj.transform.localPosition = Vector3.zero;
                    characterObj.transform.localRotation = Quaternion.identity;
                }

                var initial = new PlayerAppearanceState();
                initial.SlotIndices[(int)ClothingSlot.Face] = (byte)((playerId == 0) ? 0 : 1);
                initial.SlotIndices[(int)ClothingSlot.Shirt] = (byte)((playerId == 0) ? 0 : 4);
                initial.SlotIndices[(int)ClothingSlot.Pants] = (byte)((playerId == 0) ? 0 : 2);
                initial.SlotIndices[(int)ClothingSlot.Shoes] = (byte)((playerId == 0) ? 1 : 2);
                ApplyAppearance(characterObj, initial, null, false, log);
                return characterObj;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[InstantiateCharacter Error] {ex.Message}");
                return null;
            }
        }

        public static GameObject InstantiateCustomModel(string name, Transform parent, Action<string> log = null)
        {
            if (charPrefab == null && !LoadAssets(log)) return null;

            try
            {
                GameObject characterObj = UnityEngine.Object.Instantiate(charPrefab);
                characterObj.name = string.IsNullOrEmpty(name) ? "CustomCharModel" : name;
                if (parent != null)
                {
                    characterObj.transform.SetParent(parent, false);
                    characterObj.transform.localPosition = Vector3.zero;
                    characterObj.transform.localRotation = Quaternion.identity;
                    characterObj.transform.localScale = Vector3.one;
                }
                return characterObj;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[InstantiateCustomModel Error] {ex.Message}");
                return null;
            }
        }

        public static void ApplyAppearance(
            GameObject characterObj,
            PlayerAppearanceState state,
            CustomSkinTextureManager texManager,
            bool hideHeadgearForHelmet = false,
            Action<string> log = null)
        {
            if (characterObj == null) return;
            if (!IsLoaded) LoadAssets(log);

            PlayerAppearanceState activeState = state ?? DefaultState;

            try
            {
                CharacterMaterialCache cache = EnsureMaterialCache(characterObj);

                if (cache.GlassesRoot != null && cache.GlassesRoot.childCount > 0)
                {
                    int glassIdx = Mathf.Clamp(activeState.SlotIndices[(int)ClothingSlot.Glasses], 0, cache.GlassesRoot.childCount - 1);
                    for (int i = 0; i < cache.GlassesRoot.childCount; i++)
                    {
                        cache.GlassesRoot.GetChild(i).gameObject.SetActive(i == glassIdx);
                    }
                }

                if (cache.HeadgearRoot != null && cache.HeadgearRoot.childCount > 0)
                {
                    int headIdx = Mathf.Clamp(activeState.SlotIndices[(int)ClothingSlot.Headgear], 0, cache.HeadgearRoot.childCount - 1);
                    for (int i = 0; i < cache.HeadgearRoot.childCount; i++)
                    {
                        cache.HeadgearRoot.GetChild(i).gameObject.SetActive(!hideHeadgearForHelmet && i == headIdx);
                    }
                }

                if (cache.FaceMat != null)
                {
                    Texture2D faceTex = ResolveSlotTexture(activeState, texManager, CustomTexSlot.Face, ClothingSlot.Face, faces);
                    if (faceTex != null) cache.FaceMat.mainTexture = faceTex;
                }

                if (cache.ShirtMat != null)
                {
                    Texture2D shirtTex = ResolveSlotTexture(activeState, texManager, CustomTexSlot.Shirt, ClothingSlot.Shirt, shirts);
                    if (shirtTex != null) cache.ShirtMat.mainTexture = shirtTex;
                }

                if (cache.PantsMat != null)
                {
                    Texture2D pantsTex = ResolveSlotTexture(activeState, texManager, CustomTexSlot.Pants, ClothingSlot.Pants, pants);
                    if (pantsTex != null) cache.PantsMat.mainTexture = pantsTex;
                }

                int shoeIdx = activeState.SlotIndices[(int)ClothingSlot.Shoes];
                bool showShoes = shoeIdx > 0;

                if (cache.ShoeLeftRenderer != null)
                {
                    cache.ShoeLeftRenderer.gameObject.SetActive(showShoes);
                }
                if (cache.ShoeRightRenderer != null)
                {
                    cache.ShoeRightRenderer.gameObject.SetActive(showShoes);
                }

                if (showShoes && cache.ShoeMat != null)
                {
                    Texture2D shoeTex = ResolveCustomTexture(activeState, texManager, CustomTexSlot.Shoes);
                    if (shoeTex == null && shoes != null && shoes.Length > 0)
                    {
                        int builtInIdx = Mathf.Clamp(shoeIdx - 1, 0, shoes.Length - 1);
                        shoeTex = shoes[builtInIdx];
                    }
                    if (shoeTex != null) cache.ShoeMat.mainTexture = shoeTex;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[ApplyAppearance Error] {ex.Message}");
            }
        }

        private static Texture2D ResolveSlotTexture(
            PlayerAppearanceState state,
            CustomSkinTextureManager texManager,
            CustomTexSlot customSlot,
            ClothingSlot clothingSlot,
            Texture2D[] builtInTextures)
        {
            Texture2D customTex = ResolveCustomTexture(state, texManager, customSlot);
            if (customTex != null) return customTex;

            if (builtInTextures == null || builtInTextures.Length == 0) return null;
            int idx = Mathf.Clamp(state.SlotIndices[(int)clothingSlot], 0, builtInTextures.Length - 1);
            return builtInTextures[idx];
        }

        private static Texture2D ResolveCustomTexture(
            PlayerAppearanceState state,
            CustomSkinTextureManager texManager,
            CustomTexSlot customSlot)
        {
            if (texManager == null || state == null || !state.HasCustomTexture(customSlot))
            {
                return null;
            }
            return texManager.GetTextureByCrc(state.CustomCrc32[(int)customSlot], state.IsPointFiltered(customSlot));
        }

        private static CharacterMaterialCache EnsureMaterialCache(GameObject characterObj)
        {
            var cache = characterObj.GetComponent<CharacterMaterialCache>();
            if (cache == null)
            {
                cache = characterObj.AddComponent<CharacterMaterialCache>();
            }
            if (cache.Initialized) return cache;

            Transform bodyTf = characterObj.transform.Find(BodyMeshPath);
            if (bodyTf != null)
            {
                cache.BodyRenderer = bodyTf.GetComponent<SkinnedMeshRenderer>();
                if (cache.BodyRenderer != null)
                {
                    Material[] srcMats = cache.BodyRenderer.sharedMaterials;
                    if (srcMats != null && srcMats.Length >= 3)
                    {
                        cache.ShirtMat = new Material(srcMats[0]);
                        cache.PantsMat = new Material(srcMats[1]);
                        cache.FaceMat = new Material(srcMats[2]);
                        cache.BodyRenderer.materials = new Material[] { cache.ShirtMat, cache.PantsMat, cache.FaceMat };
                    }
                }
            }

            Transform shoeLTf = characterObj.transform.Find(ShoeLeftPath);
            Transform shoeRTf = characterObj.transform.Find(ShoeRightPath);
            if (shoeLTf != null) cache.ShoeLeftRenderer = shoeLTf.GetComponent<MeshRenderer>();
            if (shoeRTf != null) cache.ShoeRightRenderer = shoeRTf.GetComponent<MeshRenderer>();

            if (cache.ShoeLeftRenderer != null)
            {
                Material srcShoeMat = cache.ShoeLeftRenderer.sharedMaterial ?? cache.ShoeLeftRenderer.material;
                if (srcShoeMat != null)
                {
                    cache.ShoeMat = new Material(srcShoeMat);
                    cache.ShoeLeftRenderer.material = cache.ShoeMat;
                    if (cache.ShoeRightRenderer != null)
                    {
                        cache.ShoeRightRenderer.material = cache.ShoeMat;
                    }
                }
            }

            cache.GlassesRoot = characterObj.transform.Find(GlassesPath);
            cache.HeadgearRoot = characterObj.transform.Find(HeadgearPath);
            cache.Initialized = true;
            return cache;
        }

        private static GameObject LoadPrefabAsset(
            AssetBundle bundle,
            string primaryAssetName,
            string shortName,
            ref GameObject[] allGameObjectsCache)
        {
            GameObject prefab = bundle.LoadAsset<GameObject>(primaryAssetName);
            if (prefab != null) return prefab;

            prefab = bundle.LoadAsset<GameObject>(shortName);
            if (prefab != null) return prefab;

            if (allGameObjectsCache == null)
            {
                allGameObjectsCache = bundle.LoadAllAssets<GameObject>();
            }
            if (allGameObjectsCache != null)
            {
                for (int i = 0; i < allGameObjectsCache.Length; i++)
                {
                    GameObject go = allGameObjectsCache[i];
                    if (go != null && string.Equals(go.name, shortName, StringComparison.OrdinalIgnoreCase))
                    {
                        return go;
                    }
                }
                for (int i = 0; i < allGameObjectsCache.Length; i++)
                {
                    GameObject go = allGameObjectsCache[i];
                    if (go != null && go.name.IndexOf(shortName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return go;
                    }
                }
            }
            return null;
        }

        private static void CacheHeadAccessoryMetadata(GameObject prefab)
        {
            glassesCount = 0;
            headgearCount = 0;
            glassesNames = new string[0];
            headgearNames = new string[0];

            if (prefab == null) return;

            Transform glassesTf = prefab.transform.Find(GlassesPath);
            if (glassesTf != null)
            {
                glassesCount = glassesTf.childCount;
                glassesNames = new string[glassesCount];
                for (int i = 0; i < glassesCount; i++)
                {
                    glassesNames[i] = glassesTf.GetChild(i).name;
                }
            }

            Transform headEndTf = prefab.transform.Find(HeadgearPath);
            if (headEndTf != null)
            {
                headgearCount = headEndTf.childCount;
                headgearNames = new string[headgearCount];
                for (int i = 0; i < headgearCount; i++)
                {
                    headgearNames[i] = headEndTf.GetChild(i).name;
                }
            }
        }

        private static void LoadCustomizationTextures()
        {
            if (clothesBundle == null) return;

            faces = new Texture2D[SkinCatalogConstants.NumBuiltInFaces];
            pants = new Texture2D[SkinCatalogConstants.NumBuiltInPants];
            shirts = new Texture2D[SkinCatalogConstants.NumBuiltInShirts];
            shoes = new Texture2D[SkinCatalogConstants.NumBuiltInShoes];

            int maxCount = Math.Max(
                Math.Max(SkinCatalogConstants.NumBuiltInFaces, SkinCatalogConstants.NumBuiltInPants),
                Math.Max(SkinCatalogConstants.NumBuiltInShirts, SkinCatalogConstants.NumBuiltInShoes));

            for (int i = 1; i <= maxCount; i++)
            {
                string numStr = (i < 10) ? ("0" + i.ToString()) : i.ToString();
                if (i <= faces.Length) faces[i - 1] = clothesBundle.LoadAsset<Texture2D>($"char_face{numStr}.png");
                if (i <= pants.Length) pants[i - 1] = clothesBundle.LoadAsset<Texture2D>($"char_pants{numStr}.png");
                if (i <= shirts.Length) shirts[i - 1] = clothesBundle.LoadAsset<Texture2D>($"char_shirt{numStr}.png");
                if (i <= shoes.Length) shoes[i - 1] = clothesBundle.LoadAsset<Texture2D>($"char_shoes{numStr}.png");
            }
        }

        internal sealed class CharacterMaterialCache : MonoBehaviour
        {
            public SkinnedMeshRenderer BodyRenderer;
            public Material ShirtMat;
            public Material PantsMat;
            public Material FaceMat;
            public MeshRenderer ShoeLeftRenderer;
            public MeshRenderer ShoeRightRenderer;
            public Material ShoeMat;
            public Transform GlassesRoot;
            public Transform HeadgearRoot;
            public bool Initialized;

            private void OnDestroy()
            {
                if (ShirtMat != null) Destroy(ShirtMat);
                if (PantsMat != null) Destroy(PantsMat);
                if (FaceMat != null) Destroy(FaceMat);
                if (ShoeMat != null) Destroy(ShoeMat);
            }
        }
    }
}
