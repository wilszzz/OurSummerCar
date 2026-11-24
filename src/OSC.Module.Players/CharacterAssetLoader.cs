using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace OSC.Module.Players
{
    public static class CharacterAssetLoader
    {
        private static AssetBundle clothesBundle;
        private static GameObject charPrefab;

        private static Texture2D[] faces;
        private static Texture2D[] shirts;
        private static Texture2D[] pants;
        private static Texture2D[] shoes;

        public static bool IsLoaded => charPrefab != null;
        public static bool HasAttemptedLoad { get; private set; }
        public static string LoadedPath { get; private set; } = string.Empty;
        public static string StatusMessage { get; private set; } = "Не загружен";

        public static bool LoadAssets(Action<string> log = null, bool force = false)
        {
            if (charPrefab != null) return true;
            if (HasAttemptedLoad && !force) return false;
            HasAttemptedLoad = true;

            try
            {
                string myDocs = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
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

                charPrefab = clothesBundle.LoadAsset<GameObject>("char.prefab") ?? clothesBundle.LoadAsset<GameObject>("char");
                if (charPrefab == null)
                {
                    var allGo = clothesBundle.LoadAllAssets<GameObject>();
                    if (allGo != null)
                    {
                        for (int i = 0; i < allGo.Length; i++)
                        {
                            if (allGo[i] != null && allGo[i].name.IndexOf("char", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                charPrefab = allGo[i];
                                break;
                            }
                        }
                    }
                }

                if (charPrefab == null)
                {
                    StatusMessage = "char.prefab не найден";
                    return false;
                }

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

        private static void LoadCustomizationTextures()
        {
            if (clothesBundle == null) return;

            faces = new Texture2D[17];
            pants = new Texture2D[21];
            shirts = new Texture2D[47];
            shoes = new Texture2D[7];

            for (int i = 1; i <= 47; i++)
            {
                string numStr = (i < 10) ? $"0{i}" : i.ToString();
                if (i <= 17) faces[i - 1] = clothesBundle.LoadAsset<Texture2D>($"char_face{numStr}.png");
                if (i <= 21) pants[i - 1] = clothesBundle.LoadAsset<Texture2D>($"char_pants{numStr}.png");
                if (i <= 47) shirts[i - 1] = clothesBundle.LoadAsset<Texture2D>($"char_shirt{numStr}.png");
                if (i <= 7) shoes[i - 1] = clothesBundle.LoadAsset<Texture2D>($"char_shoes{numStr}.png");
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

                ApplyClothing(characterObj, playerId);
                return characterObj;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[InstantiateCharacter Error] {ex.Message}");
                return null;
            }
        }

        private static void ApplyClothing(GameObject characterObj, int playerId)
        {
            try
            {
                var bodyRenderer = characterObj.transform.Find("char/bodymesh")?.GetComponent<SkinnedMeshRenderer>();
                if (bodyRenderer != null)
                {
                    Material shirtMat = new Material(bodyRenderer.materials[0]);
                    Material pantsMat = new Material(bodyRenderer.materials[1]);
                    Material faceMat = new Material(bodyRenderer.materials[2]);

                    int shirtIdx = (playerId == 0) ? 0 : 4;
                    int pantsIdx = (playerId == 0) ? 0 : 2;
                    int faceIdx = (playerId == 0) ? 0 : 1;

                    if (shirts != null && shirtIdx < shirts.Length && shirts[shirtIdx] != null) shirtMat.mainTexture = shirts[shirtIdx];
                    if (pants != null && pantsIdx < pants.Length && pants[pantsIdx] != null) pantsMat.mainTexture = pants[pantsIdx];
                    if (faces != null && faceIdx < faces.Length && faces[faceIdx] != null) faceMat.mainTexture = faces[faceIdx];

                    bodyRenderer.materials = new Material[] { shirtMat, pantsMat, faceMat };
                }

                var shoeL = characterObj.transform.Find("char/skeleton/thig_left/knee_left/ankle_left/shoeLeft")?.GetComponent<MeshRenderer>();
                var shoeR = characterObj.transform.Find("char/skeleton/thig_right/knee_right/ankle_right/shoeRight")?.GetComponent<MeshRenderer>();
                if (shoeL != null && shoeR != null && shoes != null)
                {
                    int shoeIdx = (playerId == 0) ? 0 : 1;
                    if (shoeIdx < shoes.Length && shoes[shoeIdx] != null)
                    {
                        Material shoeMat = new Material(shoeL.material);
                        shoeMat.mainTexture = shoes[shoeIdx];
                        shoeL.material = shoeMat;
                        shoeR.material = shoeMat;
                    }
                }
            }
            catch { }
        }
    }
}
