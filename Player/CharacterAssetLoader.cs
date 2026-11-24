using System;
using System.IO;
using MSCLoader;
using UnityEngine;

namespace OurSummerCar.Player
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
        public static bool HasAttemptedLoad { get; private set; } = false;
        public static string LoadedPath { get; private set; } = string.Empty;
        public static string StatusMessage { get; private set; } = "Не загружен";

        public static bool LoadAssets(bool force = false)
        {
            if (charPrefab != null) return true;
            if (HasAttemptedLoad && !force) return false;
            HasAttemptedLoad = true;

            try
            {
                // Поиск файла clothes.unity3d по всем возможным директориям
                string myDocs = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
                string executingDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                
                var candidates = new System.Collections.Generic.List<string>();

                // 1. Рядом с загруженной DLL мода
                if (!string.IsNullOrEmpty(executingDir))
                {
                    candidates.Add(Path.Combine(executingDir, "clothes.unity3d"));
                    candidates.Add(Path.Combine(executingDir, "Assets/OurSummerCar/clothes.unity3d"));
                    candidates.Add(Path.Combine(executingDir, "Assets/clothes.unity3d"));
                    candidates.Add(Path.Combine(executingDir, "OurSummerCar/clothes.unity3d"));
                }

                // 2. Стандартные пути My Documents
                candidates.Add(Path.Combine(myDocs, "MySummerCar/Mods/Assets/OurSummerCar/clothes.unity3d"));
                candidates.Add(Path.Combine(myDocs, "MySummerCar/Mods/Assets/clothes.unity3d"));
                candidates.Add(Path.Combine(myDocs, "MySummerCar/Mods/clothes.unity3d"));
                candidates.Add(Path.Combine(myDocs, "MySummerCar/Mods/OurSummerCar/clothes.unity3d"));

                // 3. Каталог игры My Summer Car
                candidates.Add(Path.Combine(Application.dataPath, "../Mods/Assets/OurSummerCar/clothes.unity3d"));
                candidates.Add(Path.Combine(Application.dataPath, "../Mods/Assets/clothes.unity3d"));
                candidates.Add(Path.Combine(Application.dataPath, "../Mods/clothes.unity3d"));
                candidates.Add(Path.Combine(Application.dataPath, "../Mods/OurSummerCar/clothes.unity3d"));

#if DEBUG
                // 4. Локальный путь проекта разработки (только в DEBUG сборке)
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
                    StatusMessage = "clothes.unity3d не найден в Mods/Assets/OurSummerCar!";
                    ModConsole.Print("<color=yellow>[OurSummerCar]</color> clothes.unity3d не найден. Проверьте папку Mods/Assets/OurSummerCar/");
                    return false;
                }

                byte[] bundleBytes = File.ReadAllBytes(bundlePath);
                if (bundleBytes == null || bundleBytes.Length == 0)
                {
                    StatusMessage = "Файл clothes.unity3d пуст!";
                    ModConsole.Print("<color=red>[OurSummerCar]</color> clothes.unity3d пуст.");
                    return false;
                }

                // В Unity 5.0 (5.0.0f4) AssetBundle.CreateFromFile(string) поддерживает ТОЛЬКО несжатые (UnityRaw) бандлы.
                // При попытке загрузить сжатый LZMA-бандл (UnityWeb) CreateFromFile вызывает Access Violation 0xc0000005 в mysummercar.exe.
                // Поэтому бандл загружается через CreateFromMemoryImmediate, как это делает сам MSCLoader (LoadAssets.LoadBundle).
                clothesBundle = AssetBundle.CreateFromMemoryImmediate(bundleBytes);

                if (clothesBundle == null)
                {
                    StatusMessage = "Ошибка десериализации AssetBundle";
                    ModConsole.Print("<color=red>[OurSummerCar]</color> Ошибка создания AssetBundle из памяти.");
                    return false;
                }

                charPrefab = clothesBundle.LoadAsset<GameObject>("char.prefab");
                if (charPrefab == null)
                {
                    charPrefab = clothesBundle.LoadAsset<GameObject>("char");
                }
                if (charPrefab == null)
                {
                    var allGo = clothesBundle.LoadAllAssets<GameObject>();
                    if (allGo != null && allGo.Length > 0)
                    {
                        foreach (var go in allGo)
                        {
                            if (go != null && go.name.IndexOf("char", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                charPrefab = go;
                                break;
                            }
                        }
                    }
                }

                if (charPrefab == null)
                {
                    StatusMessage = "char.prefab не найден внутри clothes.unity3d";
                    ModConsole.Print("<color=red>[OurSummerCar]</color> char.prefab не найден в бандле.");
                    return false;
                }

                LoadCustomizationTextures();
                LoadedPath = bundlePath;
                StatusMessage = "Загружен (" + Path.GetFileName(bundlePath) + ")";
                ModConsole.Print("<color=green>[OurSummerCar]</color> 3D-модель персонажа и текстуры WreckMP успешно загружены из: " + bundlePath);
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = "Исключение: " + ex.Message;
                ModConsole.Print($"[CharacterAssetLoader Error] {ex.Message}");
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

                if (i <= 17)
                {
                    faces[i - 1] = clothesBundle.LoadAsset<Texture2D>($"char_face{numStr}.png");
                }
                if (i <= 21)
                {
                    pants[i - 1] = clothesBundle.LoadAsset<Texture2D>($"char_pants{numStr}.png");
                }
                if (i <= 47)
                {
                    shirts[i - 1] = clothesBundle.LoadAsset<Texture2D>($"char_shirt{numStr}.png");
                }
                if (i <= 7)
                {
                    shoes[i - 1] = clothesBundle.LoadAsset<Texture2D>($"char_shoes{numStr}.png");
                }
            }
        }

        public static GameObject InstantiateCharacter(int playerId, Transform parent)
        {
            if (charPrefab == null && !LoadAssets()) return null;

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
                ModConsole.Print($"[InstantiateCharacter Error] {ex.Message}");
                return null;
            }
        }

        private static void ApplyClothing(GameObject characterObj, int playerId)
        {
            try
            {
                // Настройка материалов одежды и лица
                var bodyRenderer = characterObj.transform.Find("char/bodymesh")?.GetComponent<SkinnedMeshRenderer>();
                if (bodyRenderer != null)
                {
                    // Клонируем материалы, чтобы не перезаписывать общий префаб
                    Material shirtMat = new Material(bodyRenderer.materials[0]);
                    Material pantsMat = new Material(bodyRenderer.materials[1]);
                    Material faceMat = new Material(bodyRenderer.materials[2]);

                    // Текстуры для Хоста (зеленый/классический стиль) и Клиента (синий amis стиль)
                    int shirtIdx = (playerId == 0) ? 0 : 4;  // char_shirt01 vs char_shirt05
                    int pantsIdx = (playerId == 0) ? 0 : 2;  // char_pants01 vs char_pants03
                    int faceIdx = (playerId == 0) ? 0 : 1;   // char_face01 vs char_face02

                    if (shirts != null && shirtIdx < shirts.Length && shirts[shirtIdx] != null)
                    {
                        shirtMat.mainTexture = shirts[shirtIdx];
                    }
                    if (pants != null && pantsIdx < pants.Length && pants[pantsIdx] != null)
                    {
                        pantsMat.mainTexture = pants[pantsIdx];
                    }
                    if (faces != null && faceIdx < faces.Length && faces[faceIdx] != null)
                    {
                        faceMat.mainTexture = faces[faceIdx];
                    }

                    bodyRenderer.materials = new Material[] { shirtMat, pantsMat, faceMat };
                }

                // Настройка обуви
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
            catch (Exception ex)
            {
                ModConsole.Print($"[ApplyClothing Error] {ex.Message}");
            }
        }
    }
}
