#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SlotMachine.Audio;
using SlotMachine.Core;
using SlotMachine.Reels;
using SlotMachine.UI;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SlotMachine.EditorTools
{
    /// <summary>
    /// Idempotent project builder. It configures texture importers, slices the supplied sprite
    /// sheets, creates the symbol data assets and prefabs, builds MainScene entirely through the
    /// Unity API, wires every serialized reference, and applies WebGL player settings.
    ///
    /// No .unity YAML is authored by hand. Re-running the menu item rebuilds generated content from
    /// source assets while leaving the raw artwork untouched.
    /// </summary>
    public static class SlotGameProjectBuilder
    {
        private const string RawFolder = "Assets/UI/Raw/Slot Machine";
        private const string DataFolder = "Assets/Data";
        private const string PrefabFolder = "Assets/Prefabs";
        private const string SceneFolder = "Assets/Scenes";
        private const string SoundsFolder = "Assets/Sounds";
        private const string AnimationsFolder = "Assets/Animations";
        private const string ScenePath = "Assets/Scenes/MainScene.unity";
        private const string SymbolCellPrefabPath = "Assets/Prefabs/SymbolCell.prefab";
        private const string ReelStripPrefabPath = "Assets/Prefabs/ReelStrip.prefab";
        private const string GeneratedFolder = "Assets/UI/Generated";
        private const string CabinetGlowTexturePath = "Assets/UI/Generated/PurpleRadialGlow.asset";
        private const string PlinthFadeTexturePath = "Assets/UI/Generated/StageBase.asset";
        private const int GlowTextureSize = 256;

        private const string BackgroundFile = "bg_gradient.png";
        private const string CabinetFile = "slot-machine4.png";
        private const string ReelBackingFile = "slot-machine5.png";
        private const string LeverUpFile = "slot-machine2.png";
        private const string LeverDownFile = "slot-machine3.png";
        private const string SevenFile = "slot-symbol1.png";
        private const string CherryFile = "slot-symbol2.png";
        private const string BellFile = "slot-symbol3.png";
        private const string BarFile = "slot-symbol4.png";
        private const string PanelFile = "popup.png";
        private const string BannerFile = "slot_machine_Middle_box.png";
        private const string LeftArrowFile = "slot_machine_buttons-04.png";
        private const string RightArrowFile = "slot_machine_buttons-03.png";
        private const string CloseFile = "slot_machine_buttons-02.png";
        private const string YesNoFile = "Yes_No_Btn.png";

        // Source cabinet geometry (816 x 624). Windows are the exact transparent cutouts in
        // slot-machine4; slot-machine5 contains the matching cyan backing strips.
        private const float SourceWidth = 816f;
        private const float SourceHeight = 624f;
        private const float SourceCenterX = 408f;
        private const float SourceCenterY = 312f;
        private const float CabinetHeight = 918f;
        private const float WindowSourceY = 246f;
        private const float WindowSourceHeight = 210f;
        private const float WindowSourceWidth = 108f;
        private static readonly float[] WindowSourceCentersX = { 282.5f, 412.5f, 542.5f };

        // Comfortable safe-area inset for corner HUD panels on the 1920x1080 reference canvas.
        private const float HudSafeMargin = 48f;

        // The cabinet and its reels are scaled/lifted as one group so the machine never gets
        // sliced by the bottom of a non-16:9 viewport.
        private const float MachineScale = 0.94f;
        private const float MachineLift = 16f;

        private static readonly Color Gold = new Color(1f, 0.79f, 0.28f, 1f);
        private static readonly Color Cream = new Color(1f, 0.96f, 0.87f, 1f);
        private static readonly Color Muted = new Color(0.72f, 0.76f, 0.88f, 1f);
        private static readonly Color PaylineGold = new Color(1f, 0.78f, 0.22f, 0.95f);
        private static readonly Color PaylineBandGold = new Color(1f, 0.78f, 0.22f, 0.11f);
        private static Font _cachedFont;
        private static Sprite _panelSprite;
        private static Sprite _bannerSprite;

        [MenuItem("Tools/Slot Game/Build Complete Project", priority = 0)]
        public static void BuildCompleteProject()
        {
            if (!PrepareScene())
            {
                return;
            }

            EnsureFolders();
            ConfigureImporters();
            AssetDatabase.Refresh();

            SymbolData[] symbols = CreateSymbolAssets();
            GameObject cellPrefab = CreateSymbolCellPrefab();
            GameObject reelStripPrefab = CreateReelStripPrefab(cellPrefab, symbols);

            BuildScene(symbols, reelStripPrefab);
            ConfigureBuildSettings();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            int missingRefs = ValidateSceneReferences();
            if (missingRefs > 0)
            {
                Debug.LogWarning($"[SlotGame] Build finished with {missingRefs} missing serialized reference(s). Run Tools/Slot Game/Validate Project for details.");
            }
            else
            {
                Debug.Log("[SlotGame] Build complete. MainScene is wired and added to Build Settings.");
            }
        }

        [MenuItem("Tools/Slot Game/Build WebGL", priority = 20)]
        public static void BuildWebGL()
        {
            BuildCompleteProject();

            string outputPath = GetBuildOutputPath();
            Directory.CreateDirectory(outputPath);

            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = outputPath,
                target = BuildTarget.WebGL,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"WebGL build failed: {report.summary.result}. See the Unity console for details.");
            }

            Debug.Log($"[SlotGame] WebGL build succeeded: {report.summary.totalSize / (1024f * 1024f):0.0} MB at {outputPath}");
        }

        /// <summary>Batch-mode entry point used by CI or the Unity CLI.</summary>
        public static void BuildWebGLFromCommandLine()
        {
            BuildWebGL();
        }

        [MenuItem("Tools/Slot Game/Validate Project", priority = 40)]
        public static void ValidateProject()
        {
            if (!File.Exists(ScenePath))
            {
                Debug.LogError($"[SlotGame] Scene not found at {ScenePath}. Run Build Complete Project first.");
                return;
            }

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            int missing = ValidateSceneReferences();

            if (missing == 0)
            {
                Debug.Log("[SlotGame] Validation passed: scene exists, all serialized object references are assigned, and MainScene is in Build Settings.");
            }
        }

        private static bool PrepareScene()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return false;
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            return true;
        }

        private static void EnsureFolders()
        {
            EnsureFolder("Assets/Data");
            EnsureFolder("Assets/Editor");
            EnsureFolder("Assets/Prefabs");
            EnsureFolder("Assets/Scenes");
            EnsureFolder("Assets/UI");
            EnsureFolder(GeneratedFolder);
            EnsureFolder("Assets/Sounds");
            EnsureFolder("Assets/Animations");

            WriteDocumentation(
                $"{SoundsFolder}/README.md",
                "# Generated Audio\n\nAll sound effects are synthesized at runtime by `ProceduralSfxController` using `AudioClip.Create`. " +
                "No external .wav or .mp3 assets are required for the WebGL build. This folder is reserved for optional authored audio replacements.\n");

            WriteDocumentation(
                $"{AnimationsFolder}/README.md",
                "# Motion\n\nReel acceleration, overshoot, and settle-back are driven by `AnimationCurve` fields on `ReelController`. " +
                "The lever pull and UI banners are coroutine-driven so the animation timing can be tuned in one place without external .anim assets.\n");
        }

        private static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];

            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        private static void WriteDocumentation(string path, string content)
        {
            if (File.Exists(path) && File.ReadAllText(path) == content)
            {
                return;
            }

            File.WriteAllText(path, content);
            AssetDatabase.ImportAsset(path);
        }

        private static void ConfigureImporters()
        {
            ConfigureSingle($"{RawFolder}/{CabinetFile}", FilterMode.Point);
            ConfigureSingle($"{RawFolder}/{ReelBackingFile}", FilterMode.Point);
            ConfigureSingle($"{RawFolder}/{LeverUpFile}", FilterMode.Point);
            ConfigureSingle($"{RawFolder}/{LeverDownFile}", FilterMode.Point);
            ConfigureSingle($"{RawFolder}/{SevenFile}", FilterMode.Point);
            ConfigureSingle($"{RawFolder}/{CherryFile}", FilterMode.Point);
            ConfigureSingle($"{RawFolder}/{BellFile}", FilterMode.Point);
            ConfigureSingle($"{RawFolder}/{BarFile}", FilterMode.Point);
            ConfigureSingle($"{RawFolder}/{BackgroundFile}", FilterMode.Bilinear);
            ConfigureSingle($"{RawFolder}/{PanelFile}", FilterMode.Bilinear, new Vector4(90f, 90f, 90f, 90f));
            ConfigureSingle($"{RawFolder}/{BannerFile}", FilterMode.Bilinear, new Vector4(42f, 42f, 42f, 42f));

            ConfigureOneByFourSheet($"{RawFolder}/{LeftArrowFile}", "LeftArrow");
            ConfigureOneByFourSheet($"{RawFolder}/{RightArrowFile}", "RightArrow");
            ConfigureOneByFourSheet($"{RawFolder}/{CloseFile}", "Close");
            ConfigureYesNoSheet($"{RawFolder}/{YesNoFile}");
        }

        private static Sprite ConfigureSingle(string path, FilterMode filterMode, Vector4 border = default)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException($"Could not load TextureImporter for {path}.");
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100f;
            importer.filterMode = filterMode;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048;
            importer.npotScale = TextureImporterNPOTScale.None;

            TextureImporterSettings settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteAlignment = (int)SpriteAlignment.Center;
            settings.spriteMeshType = SpriteMeshType.FullRect;
            settings.spriteExtrude = 0;
            settings.spriteBorder = border;
            settings.filterMode = filterMode;
            settings.mipmapEnabled = false;
            settings.wrapMode = TextureWrapMode.Clamp;
            settings.alphaIsTransparency = true;
            importer.SetTextureSettings(settings);

            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private static void ConfigureOneByFourSheet(string path, string spritePrefix)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (importer == null || texture == null)
            {
                throw new InvalidOperationException($"Could not load sprite sheet at {path}.");
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.spritePixelsPerUnit = 100f;
            importer.filterMode = FilterMode.Bilinear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048;
            importer.npotScale = TextureImporterNPOTScale.None;

            int cellWidth = texture.width;
            int cellHeight = texture.height / 4;
            string[] stateNames = { "Normal", "Hover", "Pressed", "Disabled" };
            SpriteMetaData[] metadata = new SpriteMetaData[4];

            for (int row = 0; row < 4; row++)
            {
                metadata[row] = new SpriteMetaData
                {
                    name = $"{spritePrefix}_{stateNames[row]}",
                    rect = new Rect(0f, texture.height - ((row + 1) * cellHeight), cellWidth, cellHeight),
                    alignment = (int)SpriteAlignment.Center,
                    pivot = new Vector2(0.5f, 0.5f),
                    border = Vector4.zero
                };
            }

#pragma warning disable CS0618
            importer.spritesheet = metadata;
#pragma warning restore CS0618
            importer.SaveAndReimport();
        }

        private static void ConfigureYesNoSheet(string path)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (importer == null || texture == null)
            {
                throw new InvalidOperationException($"Could not load sprite sheet at {path}.");
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.spritePixelsPerUnit = 100f;
            importer.filterMode = FilterMode.Bilinear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048;
            importer.npotScale = TextureImporterNPOTScale.None;

            // The 2x3 sheet uses consistent 230 px rows, but each state's visible button sits at a
            // different vertical offset inside its cell. Slicing the exact opaque bounds keeps the
            // YES/NO artwork stationary during SpriteSwap instead of jumping between states.
            const int buttonWidth = 371;
            const int buttonHeight = 142;
            int[] yesTop = { 83, 274, 465 };
            int[] noTop = { 83, 274, 465 };
            int[] yesLeft = { 70, 70, 70 };
            int[] noLeft = { 548, 548, 548 };
            string[] states = { "Normal", "Hover", "Pressed" };

            SpriteMetaData[] metadata = new SpriteMetaData[6];
            for (int row = 0; row < 3; row++)
            {
                metadata[row * 2] = new SpriteMetaData
                {
                    name = $"Yes_{states[row]}",
                    rect = new Rect(yesLeft[row], texture.height - (yesTop[row] + buttonHeight), buttonWidth, buttonHeight),
                    alignment = (int)SpriteAlignment.Center,
                    pivot = new Vector2(0.5f, 0.5f),
                    border = Vector4.zero
                };

                metadata[(row * 2) + 1] = new SpriteMetaData
                {
                    name = $"No_{states[row]}",
                    rect = new Rect(noLeft[row], texture.height - (noTop[row] + buttonHeight), buttonWidth, buttonHeight),
                    alignment = (int)SpriteAlignment.Center,
                    pivot = new Vector2(0.5f, 0.5f),
                    border = Vector4.zero
                };
            }

#pragma warning disable CS0618
            importer.spritesheet = metadata;
#pragma warning restore CS0618
            importer.SaveAndReimport();
        }

        private static SymbolData[] CreateSymbolAssets()
        {
            SymbolData seven = CreateSymbolAsset(
                "Symbol_7", "seven", "Seven", $"{RawFolder}/{SevenFile}", 1, 50f, 1f, true);
            SymbolData bar = CreateSymbolAsset(
                "Symbol_BAR", "bar", "BAR", $"{RawFolder}/{BarFile}", 3, 20f, 1f, false);
            SymbolData bell = CreateSymbolAsset(
                "Symbol_Bell", "bell", "Bell", $"{RawFolder}/{BellFile}", 5, 8f, 0.5f, false);
            SymbolData cherry = CreateSymbolAsset(
                "Symbol_Cherry", "cherry", "Cherry", $"{RawFolder}/{CherryFile}", 7, 2f, 0.5f, false);

            return new[] { seven, bar, bell, cherry };
        }

        private static SymbolData CreateSymbolAsset(
            string fileName,
            string id,
            string displayName,
            string spritePath,
            int weight,
            float threeOfAKindMultiplier,
            float twoOfAKindMultiplier,
            bool isJackpot)
        {
            string assetPath = $"{DataFolder}/{fileName}.asset";
            AssetDatabase.DeleteAsset(assetPath);

            SymbolData data = ScriptableObject.CreateInstance<SymbolData>();
            SerializedObject serialized = new SerializedObject(data);
            SetString(serialized, "_id", id);
            SetString(serialized, "_displayName", displayName);
            SetObject(serialized, "_sprite", LoadSingleSprite(spritePath));
            SetInt(serialized, "_weight", weight);
            SetFloat(serialized, "_threeOfAKindMultiplier", threeOfAKindMultiplier);
            SetFloat(serialized, "_twoOfAKindMultiplier", twoOfAKindMultiplier);
            SetBool(serialized, "_isJackpotSymbol", isJackpot);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            AssetDatabase.CreateAsset(data, assetPath);
            EditorUtility.SetDirty(data);
            return data;
        }

        private static GameObject CreateSymbolCellPrefab()
        {
            AssetDatabase.DeleteAsset(SymbolCellPrefabPath);

            GameObject root = new GameObject("SymbolCell", typeof(RectTransform), typeof(Image));
            RectTransform rect = root.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(160f, 103f);

            Image image = root.GetComponent<Image>();
            image.raycastTarget = false;
            image.preserveAspect = true;

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, SymbolCellPrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            return prefab;
        }

        private static GameObject CreateReelStripPrefab(GameObject cellPrefab, SymbolData[] symbols)
        {
            AssetDatabase.DeleteAsset(ReelStripPrefabPath);

            float scale = CabinetHeight / SourceHeight;
            float cellWidth = WindowSourceWidth * scale;
            float cellHeight = (WindowSourceHeight * scale) / 3f;

            GameObject root = new GameObject("ReelStrip", typeof(RectTransform));
            RectTransform rect = root.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(cellWidth, cellHeight * 3f);

            ReelController controller = root.AddComponent<ReelController>();

            GameObject cells = new GameObject("Cells", typeof(RectTransform));
            RectTransform cellsRect = cells.GetComponent<RectTransform>();
            cellsRect.SetParent(root.transform, false);
            Stretch(cellsRect);

            SerializedObject serialized = new SerializedObject(controller);
            SetObject(serialized, "_cellPrefab", cellPrefab);
            SetObject(serialized, "_cellsRoot", cellsRect);
            SetFloat(serialized, "_cellWidth", cellWidth);
            SetFloat(serialized, "_cellHeight", cellHeight);
            SetFloat(serialized, "_symbolScale", 0.88f);
            SetInt(serialized, "_cellBuffer", 2);
            SetFloat(serialized, "_baseSpeed", 1700f);
            SetFloat(serialized, "_accelerationDuration", 0.32f);
            SetFloat(serialized, "_stopDuration", 0.42f);
            SetObjectArray(serialized, "_symbols", symbols.Cast<UnityEngine.Object>().ToArray());
            SetCurve(serialized, "_accelerationCurve", CreateAccelerationCurve());
            SetCurve(serialized, "_stopCurve", CreateStopCurve());
            serialized.ApplyModifiedPropertiesWithoutUndo();

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, ReelStripPrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            return prefab;
        }

        private static AnimationCurve CreateAccelerationCurve()
        {
            return new AnimationCurve(
                new Keyframe(0f, 0.12f, 0f, 2.6f),
                new Keyframe(0.38f, 0.78f, 2.6f, 1.3f),
                new Keyframe(0.75f, 1f, 0.4f, 0f),
                new Keyframe(1f, 1f, 0f, 0f));
        }

        private static AnimationCurve CreateStopCurve()
        {
            return new AnimationCurve(
                new Keyframe(0f, 0f, 0f, 2.2f),
                new Keyframe(0.62f, 1.07f, 0.2f, 0f),
                new Keyframe(0.84f, 0.985f, 0f, 0f),
                new Keyframe(1f, 1f, 0f, 0f));
        }

        private static void BuildScene(SymbolData[] symbols, GameObject reelStripPrefab)
        {
            float scale = CabinetHeight / SourceHeight;
            Vector2 cabinetSize = new Vector2(SourceWidth * scale, CabinetHeight);
            float windowWidth = WindowSourceWidth * scale;
            float windowHeight = WindowSourceHeight * scale;
            float cellHeight = windowHeight / 3f;
            float windowCenterY = (SourceCenterY - (WindowSourceY + (WindowSourceHeight * 0.5f))) * scale;

            Sprite backgroundSprite = LoadSingleSprite($"{RawFolder}/{BackgroundFile}");
            Sprite cabinetSprite = LoadSingleSprite($"{RawFolder}/{CabinetFile}");
            Sprite backingSprite = LoadSingleSprite($"{RawFolder}/{ReelBackingFile}");
            Sprite leverUpSprite = LoadSingleSprite($"{RawFolder}/{LeverUpFile}");
            Sprite leverDownSprite = LoadSingleSprite($"{RawFolder}/{LeverDownFile}");
            _panelSprite = LoadSingleSprite($"{RawFolder}/{PanelFile}");
            _bannerSprite = LoadSingleSprite($"{RawFolder}/{BannerFile}");

            Dictionary<string, Sprite> leftArrow = LoadSheetSprites($"{RawFolder}/{LeftArrowFile}");
            Dictionary<string, Sprite> rightArrow = LoadSheetSprites($"{RawFolder}/{RightArrowFile}");
            Dictionary<string, Sprite> closeSheet = LoadSheetSprites($"{RawFolder}/{CloseFile}");
            Dictionary<string, Sprite> yesNo = LoadSheetSprites($"{RawFolder}/{YesNoFile}");

            CreateCamera();
            CreateEventSystem();

            GameObject canvasObject = new GameObject(
                "MainCanvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));

            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.pixelPerfect = false;
            canvas.sortingOrder = 0;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            scaler.referencePixelsPerUnit = 100f;

            RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();

            // The background uses a 16:9 envelope so it always covers the whole viewport, even
            // when Expand gives the canvas extra width or height on non-16:9 displays.
            Image background = CreateImage("Background", canvasRect, backgroundSprite, Vector2.zero, Vector2.zero, true);
            Stretch(background.rectTransform);
            AspectRatioFitter backgroundFitter = background.gameObject.AddComponent<AspectRatioFitter>();
            backgroundFitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            backgroundFitter.aspectRatio = 16f / 9f;
            background.raycastTarget = false;

            RectTransform gameRoot = CreateUIObject("GameRoot", canvasRect);
            Stretch(gameRoot);

            RectTransform machineRoot = CreateUIObject("MachineRoot", gameRoot);
            Stretch(machineRoot);

            // A soft indigo radial glow sits lowest inside the machine group so the cabinet and
            // lever blend into the halftone backdrop with depth. The cabinet art is cropped flat at
            // its bottom edge (slot-machine4 is opaque through its last pixel row), so a feathered
            // stage plinth and a drop shadow ground that edge instead of letting it terminate over
            // the background. All of these are siblings ordered before the cabinet, so they render
            // behind it.
            Texture2D softRadial = GetOrCreateCabinetGlowTexture();
            RawImage glow = CreateRawImage(
                "CabinetGlow",
                machineRoot,
                softRadial,
                new Vector2(cabinetSize.x * 1.45f, cabinetSize.y * 1.45f),
                Vector2.zero);
            glow.raycastTarget = false;

            // Ground the machine on its real footprint. The cabinet's opaque content spans source
            // x 137..751 (615 px, lever pedestal included), not the full 816 px sprite width, so a
            // plinth sized to the sprite would float far wider than the machine.
            float cabinetBottomY = -cabinetSize.y * 0.5f;
            const float footprintLeft = 137f;
            const float footprintRight = 751f;
            float footprintWidth = (footprintRight - footprintLeft) * scale;
            float footprintCenterX = (((footprintLeft + footprintRight) * 0.5f) - SourceCenterX) * scale;

            RawImage shadow = CreateRawImage(
                "MachineShadow",
                machineRoot,
                softRadial,
                new Vector2(footprintWidth * 1.25f, footprintWidth * 0.28f),
                new Vector2(footprintCenterX, cabinetBottomY - 46f));
            shadow.color = new Color(0.02f, 0.01f, 0.06f, 0.45f);
            shadow.raycastTarget = false;

            // One soft-edged stage base rather than a hard-edged box: the texture feathers on all
            // four sides, so the plinth dissolves into the backdrop on every axis and the cabinet's
            // cropped bottom edge lands on a lit ledge with a faint metallic lip.
            const float plinthHeight = 118f;
            RawImage plinth = CreateRawImage(
                "MachinePlinth",
                machineRoot,
                GetOrCreatePlinthFadeTexture(),
                new Vector2(footprintWidth * 1.08f, plinthHeight),
                new Vector2(footprintCenterX, cabinetBottomY + 8f - (plinthHeight * 0.5f)));
            plinth.raycastTarget = false;

            Image backing = CreateImage("ReelWindowBacking", machineRoot, backingSprite, cabinetSize, Vector2.zero, true);
            backing.type = Image.Type.Simple;

            RectTransform reelsRoot = CreateUIObject("Reels", machineRoot);
            Stretch(reelsRoot);
            ReelController[] reels = new ReelController[3];

            for (int i = 0; i < 3; i++)
            {
                RectTransform viewport = CreateUIObject($"ReelViewport_{i + 1}", reelsRoot);
                Vector2 viewportPosition = new Vector2((WindowSourceCentersX[i] - SourceCenterX) * scale, windowCenterY);
                SetFixed(viewport, viewportPosition, new Vector2(windowWidth, windowHeight));
                viewport.gameObject.AddComponent<RectMask2D>();

                // A faint amber band sits behind the reel strip, so the center row reads as the
                // active payline without covering any symbol art.
                CreatePaylineBand(viewport, windowWidth, cellHeight);

                GameObject stripObject = (GameObject)PrefabUtility.InstantiatePrefab(reelStripPrefab, viewport);
                RectTransform stripRect = stripObject.GetComponent<RectTransform>();
                Stretch(stripRect);

                ReelController reel = stripObject.GetComponent<ReelController>();
                reel.PreviewAt(i);
                reels[i] = reel;
            }

            Image cabinet = CreateImage("MachineCabinetFrame", machineRoot, cabinetSprite, cabinetSize, Vector2.zero, true);
            cabinet.raycastTarget = false;

            // Side brackets are layered above the cabinet frame but outside the masked reel
            // windows, giving the player an unmistakable center-payline cue.
            CreatePaylineBrackets(machineRoot, scale, windowCenterY, windowWidth, cellHeight);

            Image lever = CreateImage("LeverOverlay", machineRoot, leverUpSprite, cabinetSize, Vector2.zero, true);
            lever.raycastTarget = false;

            RectTransform leverHit = CreateUIObject("LeverHitArea", lever.transform);
            float leverCenterX = (718.5f - SourceCenterX) * scale;
            float leverCenterY = (SourceCenterY - ((296f + 565f) * 0.5f)) * scale;
            SetFixed(leverHit, new Vector2(leverCenterX, leverCenterY), new Vector2(91f * scale, 269f * scale));

            Image leverHitImage = leverHit.gameObject.AddComponent<Image>();
            leverHitImage.color = new Color(1f, 1f, 1f, 0f);
            leverHitImage.raycastTarget = true;

            Button leverButton = leverHit.gameObject.AddComponent<Button>();
            leverButton.targetGraphic = leverHitImage;
            leverButton.transition = Selectable.Transition.None;
            Navigation leverNavigation = leverButton.navigation;
            leverNavigation.mode = Navigation.Mode.None;
            leverButton.navigation = leverNavigation;

            // Frame the whole machine cleanly: scale around the canvas center and lift it just
            // enough to keep the cabinet base clear of the bottom edge on any aspect ratio.
            machineRoot.localScale = Vector3.one * MachineScale;
            machineRoot.anchoredPosition = new Vector2(0f, MachineLift);

            GameObject audioObject = new GameObject("Audio", typeof(AudioSource), typeof(ProceduralSfxController));
            ProceduralSfxController sfx = audioObject.GetComponent<ProceduralSfxController>();
            AudioSource audioSource = audioObject.GetComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            audioSource.spatialBlend = 0f;

            SerializedObject sfxSerialized = new SerializedObject(sfx);
            SetObject(sfxSerialized, "_audioSource", audioSource);
            SetFloat(sfxSerialized, "_masterVolume", 0.38f);
            sfxSerialized.ApplyModifiedPropertiesWithoutUndo();

            RectTransform hudRoot = CreateUIObject("HUD", canvasRect);
            Stretch(hudRoot);

            HudController hud = BuildHud(
                hudRoot,
                leftArrow,
                rightArrow,
                out Image winBannerBackground,
                out CanvasGroup winBannerGroup,
                out Text winBannerText,
                out GameObject freeSpinBadge,
                out Text freeSpinText,
                out Text balanceText,
                out Text betText,
                out Button spinButton,
                out Text spinLabel,
                out Button betDecrease,
                out Button betIncrease,
                out Button autoSpinButton,
                out Text autoSpinLabel,
                out Button paytableButton);

            RectTransform paytableRoot = CreateUIObject("PaytableModal", canvasRect);
            Stretch(paytableRoot);
            PaytableModal paytable = BuildPaytableModal(paytableRoot, symbols, closeSheet);
            paytableRoot.gameObject.SetActive(false);

            RectTransform confirmRoot = CreateUIObject("ConfirmResetModal", canvasRect);
            Stretch(confirmRoot);
            ConfirmResetModal confirm = BuildConfirmResetModal(confirmRoot, yesNo);
            confirmRoot.gameObject.SetActive(false);

            SlotMachineController machine = gameRoot.gameObject.AddComponent<SlotMachineController>();
            SerializedObject machineSerialized = new SerializedObject(machine);
            SetObjectArray(machineSerialized, "_reels", reels.Cast<UnityEngine.Object>().ToArray());
            SetObjectArray(machineSerialized, "_symbols", symbols.Cast<UnityEngine.Object>().ToArray());
            SetObject(machineSerialized, "_leverOverlay", lever);
            SetObject(machineSerialized, "_leverUpSprite", leverUpSprite);
            SetObject(machineSerialized, "_leverDownSprite", leverDownSprite);
            SetObject(machineSerialized, "_leverButton", leverButton);
            SetObject(machineSerialized, "_sfx", sfx);
            SetIntArray(machineSerialized, "_betOptions", new[] { 10, 25, 50, 100, 250 });
            SetInt(machineSerialized, "_startingBalance", 1000);
            SetFloatArray(machineSerialized, "_reelStopTimes", new[] { 1.1f, 1.6f, 2.1f });
            SetFloat(machineSerialized, "_autoSpinDelay", 1.15f);
            SetFloat(machineSerialized, "_freeSpinDelay", 0.9f);
            SetFloat(machineSerialized, "_leverDownDuration", 0.35f);
            SetFloat(machineSerialized, "_resultHoldDuration", 0.35f);
            machineSerialized.ApplyModifiedPropertiesWithoutUndo();

            SerializedObject hudSerialized = new SerializedObject(hud);
            SetObject(hudSerialized, "_machine", machine);
            SetObject(hudSerialized, "_balanceText", balanceText);
            SetObject(hudSerialized, "_betText", betText);
            SetObject(hudSerialized, "_spinButton", spinButton);
            SetObject(hudSerialized, "_spinButtonLabel", spinLabel);
            SetObject(hudSerialized, "_betDecreaseButton", betDecrease);
            SetObject(hudSerialized, "_betIncreaseButton", betIncrease);
            SetObject(hudSerialized, "_autoSpinButton", autoSpinButton);
            SetObject(hudSerialized, "_autoSpinLabel", autoSpinLabel);
            SetObject(hudSerialized, "_paytableButton", paytableButton);
            SetObject(hudSerialized, "_winBannerGroup", winBannerGroup);
            SetObject(hudSerialized, "_winBannerBackground", winBannerBackground);
            SetObject(hudSerialized, "_winBannerText", winBannerText);
            SetObject(hudSerialized, "_freeSpinBadge", freeSpinBadge);
            SetObject(hudSerialized, "_freeSpinText", freeSpinText);
            SetObject(hudSerialized, "_paytableModal", paytable);
            SetObject(hudSerialized, "_confirmResetModal", confirm);
            hudSerialized.ApplyModifiedPropertiesWithoutUndo();

            Selection.activeGameObject = canvasObject;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
            AddSceneToBuildSettings();
        }

        private static void CreatePaylineBand(RectTransform viewport, float width, float height)
        {
            Image band = CreateImage("PaylineBand", viewport, null, new Vector2(width, height), Vector2.zero, false);
            band.color = PaylineBandGold;
            band.raycastTarget = false;

            // Thin edge lines make the band legible against both the cyan backing and dark
            // cabinet while remaining behind the symbol cells.
            Image topEdge = CreateImage("PaylineBandTop", band.rectTransform, null, new Vector2(width, 2f), new Vector2(0f, height * 0.5f), false);
            topEdge.color = new Color(1f, 0.78f, 0.22f, 0.34f);
            topEdge.raycastTarget = false;

            Image bottomEdge = CreateImage("PaylineBandBottom", band.rectTransform, null, new Vector2(width, 2f), new Vector2(0f, height * -0.5f), false);
            bottomEdge.color = new Color(1f, 0.78f, 0.22f, 0.34f);
            bottomEdge.raycastTarget = false;
        }

        private static void CreatePaylineBrackets(RectTransform parent, float scale, float centerY, float windowWidth, float cellHeight)
        {
            float leftEdge = ((WindowSourceCentersX[0] - SourceCenterX) * scale) - (windowWidth * 0.5f);
            float rightEdge = ((WindowSourceCentersX[2] - SourceCenterX) * scale) + (windowWidth * 0.5f);

            CreatePaylineBracket(parent, leftEdge, centerY, cellHeight, isLeft: true);
            CreatePaylineBracket(parent, rightEdge, centerY, cellHeight, isLeft: false);
        }

        private static void CreatePaylineBracket(
            RectTransform parent,
            float windowEdgeX,
            float centerY,
            float cellHeight,
            bool isLeft)
        {
            const float gap = 11f;
            const float barWidth = 4f;
            const float tipWidth = 12f;
            const float tipHeight = 3f;

            float direction = isLeft ? -1f : 1f;
            float barX = windowEdgeX + (gap * direction);
            float tipX = barX - (6f * direction);
            float rowHalfHeight = (cellHeight * 0.5f) - 3f;

            Image bar = CreateImage(
                isLeft ? "PaylineBracketLeft" : "PaylineBracketRight",
                parent,
                null,
                new Vector2(barWidth, cellHeight + 10f),
                new Vector2(barX, centerY),
                false);
            bar.color = PaylineGold;
            bar.raycastTarget = false;

            Image topTip = CreateImage(
                isLeft ? "PaylineTipTopLeft" : "PaylineTipTopRight",
                parent,
                null,
                new Vector2(tipWidth, tipHeight),
                new Vector2(tipX, centerY + rowHalfHeight),
                false);
            topTip.color = PaylineGold;
            topTip.raycastTarget = false;

            Image bottomTip = CreateImage(
                isLeft ? "PaylineTipBottomLeft" : "PaylineTipBottomRight",
                parent,
                null,
                new Vector2(tipWidth, tipHeight),
                new Vector2(tipX, centerY - rowHalfHeight),
                false);
            bottomTip.color = PaylineGold;
            bottomTip.raycastTarget = false;
        }

        private static HudController BuildHud(
            RectTransform hudRoot,
            Dictionary<string, Sprite> leftArrow,
            Dictionary<string, Sprite> rightArrow,
            out Image winBannerBackground,
            out CanvasGroup winBannerGroup,
            out Text winBannerText,
            out GameObject freeSpinBadge,
            out Text freeSpinText,
            out Text balanceText,
            out Text betText,
            out Button spinButton,
            out Text spinLabel,
            out Button betDecrease,
            out Button betIncrease,
            out Button autoSpinButton,
            out Text autoSpinLabel,
            out Button paytableButton)
        {
            // Balance stack is vertically centered on the panel: label (26) + value (60) span 86
            // units, so the centers sit at +30 and -13 relative to the panel's vertical midpoint.
            CreateSimplePanel("BalancePanel", hudRoot, new Vector2(320f, 130f), new Vector2(-960f + HudSafeMargin + 160f, 380f), Color.white);
            CreateText("BalanceCaption", hudRoot, "BALANCE", 18, TextAnchor.MiddleCenter, Gold, new Vector2(260f, 26f), new Vector2(-960f + HudSafeMargin + 160f, 380f + 30f), true);
            balanceText = CreateText("BalanceValue", hudRoot, "1,000", 40, TextAnchor.MiddleCenter, Cream, new Vector2(290f, 60f), new Vector2(-960f + HudSafeMargin + 160f, 380f - 13f), true);

            // Bet stack is centered on the panel too, so the value aligns with the arrow buttons on
            // the panel centerline: label (24) + value (50) span 74 units, so the centers sit at
            // +26.5 and -13.5 relative to the panel midpoint.
            CreateSimplePanel("BetPanel", hudRoot, new Vector2(320f, 156f), new Vector2(-960f + HudSafeMargin + 160f, -370f), Color.white);
            CreateText("BetCaption", hudRoot, "BET", 18, TextAnchor.MiddleCenter, Gold, new Vector2(120f, 24f), new Vector2(-960f + HudSafeMargin + 160f, -370f + 26.5f), true);
            betText = CreateText("BetValue", hudRoot, "10", 34, TextAnchor.MiddleCenter, Cream, new Vector2(150f, 50f), new Vector2(-960f + HudSafeMargin + 160f, -370f - 13.5f), true);

            betDecrease = CreateSpriteButton(
                "BetDecrease",
                hudRoot,
                new Vector2(76f, 76f),
                new Vector2(-960f + HudSafeMargin + 160f - 112f, -370f),
                leftArrow["LeftArrow_Normal"],
                leftArrow["LeftArrow_Hover"],
                leftArrow["LeftArrow_Pressed"],
                leftArrow["LeftArrow_Disabled"]);
            betIncrease = CreateSpriteButton(
                "BetIncrease",
                hudRoot,
                new Vector2(76f, 76f),
                new Vector2(-960f + HudSafeMargin + 160f + 112f, -370f),
                rightArrow["RightArrow_Normal"],
                rightArrow["RightArrow_Hover"],
                rightArrow["RightArrow_Pressed"],
                rightArrow["RightArrow_Disabled"]);

            spinButton = CreatePanelButton(
                "SpinButton",
                hudRoot,
                new Vector2(360f, 112f),
                new Vector2(0f, -380f),
                _bannerSprite,
                new Color(1f, 0.98f, 0.94f, 1f),
                new Color(0.94f, 0.86f, 0.68f, 1f),
                new Color(0.58f, 0.55f, 0.58f, 0.65f));
            spinLabel = CreateText("SpinLabel", spinButton.transform, "SPIN", 38, TextAnchor.MiddleCenter, Color.white, new Vector2(320f, 44f), new Vector2(0f, 7f), true);
            CreateText("SpinHint", spinButton.transform, "SPACE", 12, TextAnchor.MiddleCenter, new Color(1f, 0.95f, 0.8f, 0.82f), new Vector2(120f, 15f), new Vector2(0f, -21f), true);

            autoSpinButton = CreatePanelButton(
                "AutoSpinButton",
                hudRoot,
                new Vector2(250f, 76f),
                new Vector2(960f - HudSafeMargin - 125f, -280f),
                _panelSprite,
                new Color(1f, 0.93f, 0.8f, 1f),
                new Color(0.8f, 0.74f, 0.62f, 1f),
                new Color(0.45f, 0.45f, 0.5f, 0.6f));
            autoSpinLabel = CreateText("AutoSpinLabel", autoSpinButton.transform, "AUTO  OFF", 22, TextAnchor.MiddleCenter, Color.white, new Vector2(220f, 40f), Vector2.zero, true);

            paytableButton = CreatePanelButton(
                "PaytableButton",
                hudRoot,
                new Vector2(250f, 76f),
                new Vector2(960f - HudSafeMargin - 125f, -382f),
                _panelSprite,
                new Color(1f, 0.93f, 0.8f, 1f),
                new Color(0.8f, 0.74f, 0.62f, 1f),
                new Color(0.45f, 0.45f, 0.5f, 0.6f));
            CreateText("PaytableLabel", paytableButton.transform, "PAYTABLE", 22, TextAnchor.MiddleCenter, Color.white, new Vector2(220f, 40f), Vector2.zero, true);

            RectTransform badge = CreateUIObject("FreeSpinBadge", hudRoot);
            SetFixed(badge, new Vector2(960f - HudSafeMargin - 125f, 390f), new Vector2(250f, 96f));
            Image badgeImage = badge.gameObject.AddComponent<Image>();
            badgeImage.sprite = _panelSprite;
            badgeImage.type = Image.Type.Simple;
            badgeImage.color = Color.white;
            badgeImage.raycastTarget = false;
            freeSpinBadge = badge.gameObject;
            freeSpinText = CreateText("FreeSpinText", badge, "FREE SPINS  5", 22, TextAnchor.MiddleCenter, Gold, new Vector2(230f, 56f), Vector2.zero, true);
            freeSpinBadge.SetActive(false);

            RectTransform banner = CreateUIObject("WinBanner", hudRoot);
            SetFixed(banner, new Vector2(0f, -260f), new Vector2(520f, 112f));
            winBannerBackground = banner.gameObject.AddComponent<Image>();
            winBannerBackground.sprite = _bannerSprite;
            winBannerBackground.type = Image.Type.Simple;
            winBannerBackground.color = Color.white;
            winBannerBackground.raycastTarget = false;
            winBannerGroup = banner.gameObject.AddComponent<CanvasGroup>();
            winBannerGroup.alpha = 0f;
            winBannerGroup.interactable = false;
            winBannerGroup.blocksRaycasts = false;
            winBannerText = CreateText("WinBannerText", banner, string.Empty, 23, TextAnchor.MiddleCenter, Cream, new Vector2(460f, 72f), Vector2.zero, true);
            winBannerText.horizontalOverflow = HorizontalWrapMode.Wrap;
            winBannerText.verticalOverflow = VerticalWrapMode.Truncate;
            banner.gameObject.SetActive(false);

            HudController hud = hudRoot.gameObject.AddComponent<HudController>();
            _ = hud;
            return hud;
        }

        private static PaytableModal BuildPaytableModal(
            RectTransform root,
            SymbolData[] symbols,
            Dictionary<string, Sprite> closeSheet)
        {
            Image dim = root.gameObject.AddComponent<Image>();
            dim.color = new Color(0.02f, 0.01f, 0.05f, 0.82f);
            dim.raycastTarget = true;

            // The source popup uses a 90 px 9-slice border, so the panel is taller than the
            // content bounds to keep every row and the rules inside the opaque center.
            Image panel = CreateSlicedPanel("PaytablePanel", root, new Vector2(1040f, 680f), Vector2.zero, Color.white);
            panel.raycastTarget = true;

            CreateText("PaytableTitle", panel.transform, "PAYTABLE", 42, TextAnchor.MiddleCenter, Gold, new Vector2(500f, 60f), new Vector2(0f, 210f), true);
            CreateText("HeaderSymbol", panel.transform, "SYMBOL", 17, TextAnchor.MiddleCenter, Muted, new Vector2(220f, 28f), new Vector2(-370f, 150f), true);
            CreateText("HeaderThree", panel.transform, "3 MATCH", 17, TextAnchor.MiddleCenter, Muted, new Vector2(180f, 28f), new Vector2(-40f, 150f), true);
            CreateText("HeaderTwo", panel.transform, "2 MATCH", 17, TextAnchor.MiddleCenter, Muted, new Vector2(180f, 28f), new Vector2(250f, 150f), true);

            Button closeButton = CreateSpriteButton(
                "PaytableClose",
                panel.transform,
                new Vector2(62f, 62f),
                new Vector2(380f, 206f),
                closeSheet["Close_Normal"],
                closeSheet["Close_Hover"],
                closeSheet["Close_Pressed"],
                closeSheet["Close_Disabled"]);

            Image[] icons = new Image[symbols.Length];
            Text[] names = new Text[symbols.Length];
            Text[] threeValues = new Text[symbols.Length];
            Text[] twoValues = new Text[symbols.Length];
            float[] rowY = { 100f, 28f, -44f, -116f };

            for (int i = 0; i < symbols.Length; i++)
            {
                float y = rowY[Mathf.Min(i, rowY.Length - 1)];
                icons[i] = CreateImage($"SymbolIcon_{i}", panel.transform, symbols[i].Sprite, new Vector2(52f, 52f), new Vector2(-370f, y), true);
                icons[i].raycastTarget = false;
                names[i] = CreateText($"SymbolName_{i}", panel.transform, symbols[i].DisplayName.ToUpperInvariant(), 25, TextAnchor.MiddleLeft, Cream, new Vector2(220f, 36f), new Vector2(-230f, y), true);
                threeValues[i] = CreateText($"SymbolThree_{i}", panel.transform, string.Empty, 26, TextAnchor.MiddleCenter, Gold, new Vector2(180f, 36f), new Vector2(-40f, y), true);
                twoValues[i] = CreateText($"SymbolTwo_{i}", panel.transform, string.Empty, 26, TextAnchor.MiddleCenter, Cream, new Vector2(180f, 36f), new Vector2(250f, y), true);

                Image separator = CreateImage($"Separator_{i}", panel.transform, null, new Vector2(880f, 1f), new Vector2(0f, y - 38f), false);
                separator.color = new Color(1f, 1f, 1f, 0.08f);
                separator.raycastTarget = false;
            }

            Text rules = CreateText("Rules", panel.transform, string.Empty, 16, TextAnchor.UpperCenter, Muted, new Vector2(920f, 48f), new Vector2(0f, -198f), false);
            rules.horizontalOverflow = HorizontalWrapMode.Wrap;
            rules.verticalOverflow = VerticalWrapMode.Truncate;
            rules.lineSpacing = 1f;

            PaytableModal modal = root.gameObject.AddComponent<PaytableModal>();
            SerializedObject serialized = new SerializedObject(modal);
            SetObject(serialized, "_closeButton", closeButton);
            SetObjectArray(serialized, "_symbols", symbols.Cast<UnityEngine.Object>().ToArray());
            SetObjectArray(serialized, "_symbolIcons", icons.Cast<UnityEngine.Object>().ToArray());
            SetObjectArray(serialized, "_symbolNames", names.Cast<UnityEngine.Object>().ToArray());
            SetObjectArray(serialized, "_threeOfAKindValues", threeValues.Cast<UnityEngine.Object>().ToArray());
            SetObjectArray(serialized, "_twoOfAKindValues", twoValues.Cast<UnityEngine.Object>().ToArray());
            SetObject(serialized, "_rulesText", rules);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return modal;
        }

        private static ConfirmResetModal BuildConfirmResetModal(RectTransform root, Dictionary<string, Sprite> yesNo)
        {
            Image dim = root.gameObject.AddComponent<Image>();
            dim.color = new Color(0.02f, 0.01f, 0.05f, 0.82f);
            dim.raycastTarget = true;

            Image panel = CreateSlicedPanel("ConfirmPanel", root, new Vector2(760f, 520f), Vector2.zero, Color.white);
            panel.raycastTarget = true;

            Text title = CreateText("ConfirmTitle", panel.transform, "OUT OF CREDITS", 34, TextAnchor.MiddleCenter, Gold, new Vector2(520f, 56f), new Vector2(0f, 125f), true);
            Text body = CreateText("ConfirmBody", panel.transform, string.Empty, 22, TextAnchor.MiddleCenter, Cream, new Vector2(620f, 80f), new Vector2(0f, 40f), false);
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
            body.verticalOverflow = VerticalWrapMode.Truncate;

            Button yesButton = CreateSpriteButton(
                "YesButton",
                panel.transform,
                new Vector2(280f, 107f),
                new Vector2(-160f, -95f),
                yesNo["Yes_Normal"],
                yesNo["Yes_Hover"],
                yesNo["Yes_Pressed"],
                yesNo["Yes_Normal"]);

            Button noButton = CreateSpriteButton(
                "NoButton",
                panel.transform,
                new Vector2(280f, 107f),
                new Vector2(160f, -95f),
                yesNo["No_Normal"],
                yesNo["No_Hover"],
                yesNo["No_Pressed"],
                yesNo["No_Normal"]);

            ConfirmResetModal modal = root.gameObject.AddComponent<ConfirmResetModal>();
            SerializedObject serialized = new SerializedObject(modal);
            SetObject(serialized, "_yesButton", yesButton);
            SetObject(serialized, "_noButton", noButton);
            SetObject(serialized, "_titleText", title);
            SetObject(serialized, "_bodyText", body);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return modal;
        }

        private static void CreateCamera()
        {
            GameObject cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, 0f, -10f);

            Camera camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.02f, 0.11f, 1f);
            camera.orthographic = true;
            camera.orthographicSize = 5.4f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 1000f;

            Type urpCameraDataType = Type.GetType(
                "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
            if (urpCameraDataType != null)
            {
                cameraObject.AddComponent(urpCameraDataType);
            }
        }

        private static void CreateEventSystem()
        {
            GameObject eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));

            Type inputSystemModuleType = Type.GetType(
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (inputSystemModuleType != null)
            {
                eventSystemObject.AddComponent(inputSystemModuleType);
            }
            else
            {
                eventSystemObject.AddComponent<StandaloneInputModule>();
            }
        }

        private static RectTransform CreateUIObject(string name, Transform parent)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.localScale = Vector3.one;
            return rect;
        }

        private static Image CreateImage(
            string name,
            Transform parent,
            Sprite sprite,
            Vector2 size,
            Vector2 position,
            bool preserveAspect)
        {
            RectTransform rect = CreateUIObject(name, parent);
            SetFixed(rect, position, size);

            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = preserveAspect;
            image.raycastTarget = false;
            return image;
        }

        private static Image CreateSlicedPanel(
            string name,
            Transform parent,
            Vector2 size,
            Vector2 position,
            Color color)
        {
            Image image = CreateImage(name, parent, _panelSprite, size, position, false);
            image.type = Image.Type.Sliced;
            image.color = color;
            image.pixelsPerUnitMultiplier = 1f;
            return image;
        }

        private static Image CreateSimplePanel(
            string name,
            Transform parent,
            Vector2 size,
            Vector2 position,
            Color color)
        {
            // HUD panels are much smaller than the authored card. Stretching the sprite avoids
            // the 90 px 9-slice border swallowing the tiny HUD rects while the large modals still
            // use true sliced rendering.
            Image image = CreateImage(name, parent, _panelSprite, size, position, false);
            image.type = Image.Type.Simple;
            image.color = color;
            return image;
        }

        private static Button CreateSpriteButton(
            string name,
            Transform parent,
            Vector2 size,
            Vector2 position,
            Sprite normal,
            Sprite hover,
            Sprite pressed,
            Sprite disabled)
        {
            RectTransform rect = CreateUIObject(name, parent);
            SetFixed(rect, position, size);

            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = normal;
            image.preserveAspect = true;
            image.raycastTarget = true;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState
            {
                highlightedSprite = hover,
                pressedSprite = pressed,
                disabledSprite = disabled
            };

            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;
            return button;
        }

        private static Button CreatePanelButton(
            string name,
            Transform parent,
            Vector2 size,
            Vector2 position,
            Sprite sprite,
            Color highlightedColor,
            Color pressedColor,
            Color disabledColor)
        {
            RectTransform rect = CreateUIObject(name, parent);
            SetFixed(rect, position, size);

            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Simple;
            image.raycastTarget = true;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;

            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = highlightedColor;
            colors.pressedColor = pressedColor;
            colors.selectedColor = Color.white;
            colors.disabledColor = disabledColor;
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;
            return button;
        }

        private static Text CreateText(
            string name,
            Transform parent,
            string value,
            int fontSize,
            TextAnchor anchor,
            Color color,
            Vector2 size,
            Vector2 position,
            bool bold)
        {
            RectTransform rect = CreateUIObject(name, parent);
            SetFixed(rect, position, size);

            Text text = rect.gameObject.AddComponent<Text>();
            text.font = GetFont();
            text.text = value;
            text.fontSize = fontSize;
            text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            text.alignment = anchor;
            text.color = color;
            text.raycastTarget = false;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.lineSpacing = 1.05f;

            Outline outline = rect.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            outline.useGraphicAlpha = true;
            return text;
        }

        private static Font GetFont()
        {
            if (_cachedFont != null)
            {
                return _cachedFont;
            }

            _cachedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_cachedFont == null)
            {
                _cachedFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            if (_cachedFont == null)
            {
                throw new InvalidOperationException("No built-in UI font is available.");
            }

            return _cachedFont;
        }

        private static Texture2D GetOrCreatePlinthFadeTexture()
        {
            // Regenerated from code on every build, so retuning the stage base never leaves a stale
            // baked asset behind.
            AssetDatabase.DeleteAsset(PlinthFadeTexturePath);

            const int width = 128;
            const int height = 96;
            Texture2D stage = new Texture2D(width, height, TextureFormat.RGBA32, false);
            stage.filterMode = FilterMode.Bilinear;
            stage.wrapMode = TextureWrapMode.Clamp;
            stage.name = "StageBase";

            Color bodyBottom = new Color(0.085f, 0.055f, 0.22f);
            Color bodyTop = new Color(0.235f, 0.17f, 0.50f);
            Color lipGold = new Color(0.93f, 0.74f, 0.34f);

            for (int y = 0; y < height; y++)
            {
                // v = 0 at the bottom row, 1 at the top row.
                float v = y / (height - 1f);
                float verticalFade = SmoothStep01(v / 0.62f);
                float bodyAlpha = 0.95f * verticalFade;
                Color row = Color.Lerp(bodyBottom, bodyTop, SmoothStep01(v / 0.75f));

                // The gold lip is baked 0.88..1.0 up the texture, and the plinth is tucked 8 px
                // behind the cabinet, so the lip surfaces as a continuous soft-edged metallic line
                // just below the cabinet's cropped bottom edge instead of peeking past its ends.
                bool isLip = v > 0.88f;
                if (isLip)
                {
                    row = lipGold;
                    bodyAlpha = 0.5f * SmoothStep01((v - 0.88f) / 0.04f);
                }

                for (int x = 0; x < width; x++)
                {
                    float u = x / (width - 1f);
                    float sideFade = SmoothStep01(u / 0.09f) * SmoothStep01((1f - u) / 0.09f);
                    stage.SetPixel(x, y, new Color(row.r, row.g, row.b, bodyAlpha * sideFade));
                }
            }

            stage.Apply(false, false);
            AssetDatabase.CreateAsset(stage, PlinthFadeTexturePath);
            return stage;
        }

        private static float SmoothStep01(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * (3f - (2f * t));
        }

        private static Texture2D GetOrCreateCabinetGlowTexture()
        {
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(CabinetGlowTexturePath);
            if (texture != null)
            {
                return texture;
            }

            texture = CreateSoftRadialGlowTexture(GlowTextureSize);
            AssetDatabase.CreateAsset(texture, CabinetGlowTexturePath);
            return texture;
        }

        private static Texture2D CreateSoftRadialGlowTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;

            Color glowColor = new Color(0.36f, 0.22f, 0.85f);
            float half = (size - 1f) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float distance = Mathf.Sqrt((dx * dx) + (dy * dy));
                    float falloff = Mathf.Clamp01(1f - distance);

                    // Smoothstep falloff: tangent-flat at the center and exactly alpha 0 at the edges,
                    // so the glow feathers into the background with no visible rim.
                    float alpha = falloff * falloff * (3f - (2f * falloff));
                    texture.SetPixel(x, y, new Color(glowColor.r, glowColor.g, glowColor.b, alpha));
                }
            }

            texture.Apply(false, false);
            return texture;
        }

        private static RawImage CreateRawImage(
            string name,
            Transform parent,
            Texture2D texture,
            Vector2 size,
            Vector2 position)
        {
            RectTransform rect = CreateUIObject(name, parent);
            SetFixed(rect, position, size);

            RawImage rawImage = rect.gameObject.AddComponent<RawImage>();
            rawImage.texture = texture;
            rawImage.color = Color.white;
            rawImage.raycastTarget = false;
            return rawImage;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetFixed(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static Sprite LoadSingleSprite(string path)
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                throw new InvalidOperationException($"Expected a Single sprite at {path}.");
            }

            return sprite;
        }

        private static Dictionary<string, Sprite> LoadSheetSprites(string path)
        {
            Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
            for (int i = 0; i < assets.Length; i++)
            {
                if (assets[i] is Sprite sprite)
                {
                    sprites[sprite.name] = sprite;
                }
            }

            if (sprites.Count == 0)
            {
                throw new InvalidOperationException($"No sliced sprites found at {path}.");
            }

            return sprites;
        }

        private static void SetCurve(SerializedObject serialized, string propertyName, AnimationCurve curve)
        {
            SerializedProperty property = FindRequiredProperty(serialized, propertyName);
            property.animationCurveValue = curve;
        }

        private static void SetObject(SerializedObject serialized, string propertyName, UnityEngine.Object value)
        {
            SerializedProperty property = FindRequiredProperty(serialized, propertyName);
            property.objectReferenceValue = value;
        }

        private static void SetObjectArray(SerializedObject serialized, string propertyName, UnityEngine.Object[] values)
        {
            SerializedProperty property = FindRequiredProperty(serialized, propertyName);
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
        }

        private static void SetIntArray(SerializedObject serialized, string propertyName, int[] values)
        {
            SerializedProperty property = FindRequiredProperty(serialized, propertyName);
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).intValue = values[i];
            }
        }

        private static void SetFloatArray(SerializedObject serialized, string propertyName, float[] values)
        {
            SerializedProperty property = FindRequiredProperty(serialized, propertyName);
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).floatValue = values[i];
            }
        }

        private static void SetString(SerializedObject serialized, string propertyName, string value)
        {
            FindRequiredProperty(serialized, propertyName).stringValue = value;
        }

        private static void SetInt(SerializedObject serialized, string propertyName, int value)
        {
            FindRequiredProperty(serialized, propertyName).intValue = value;
        }

        private static void SetFloat(SerializedObject serialized, string propertyName, float value)
        {
            FindRequiredProperty(serialized, propertyName).floatValue = value;
        }

        private static void SetBool(SerializedObject serialized, string propertyName, bool value)
        {
            FindRequiredProperty(serialized, propertyName).boolValue = value;
        }

        private static SerializedProperty FindRequiredProperty(SerializedObject serialized, string propertyName)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null)
            {
                throw new InvalidOperationException(
                    $"Serialized field '{propertyName}' not found on {serialized.targetObject.GetType().Name}.");
            }

            return property;
        }

        private static void AddSceneToBuildSettings()
        {
            string sceneGuid = AssetDatabase.AssetPathToGUID(ScenePath);
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(ScenePath, true)
            };

            Debug.Log($"[SlotGame] MainScene registered in Build Settings (GUID {sceneGuid}).");
        }

        private static void ConfigureBuildSettings()
        {
            PlayerSettings.runInBackground = true;
            PlayerSettings.defaultWebScreenWidth = 1920;
            PlayerSettings.defaultWebScreenHeight = 1080;
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.None;
            PlayerSettings.WebGL.template = "PROJECT:ResponsiveFit";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.WebGL, "com.hiring.slotmachine");

            // decompressionFallback is obsolete on some Unity versions; set it via reflection only
            // when the property still exists so this builder compiles warning-free.
            PropertyInfo fallback = typeof(PlayerSettings.WebGL).GetProperty(
                "decompressionFallback",
                BindingFlags.Public | BindingFlags.Static);
            if (fallback != null && fallback.PropertyType == typeof(bool) && fallback.CanWrite)
            {
                fallback.SetValue(null, true);
            }
        }

        private static int ValidateSceneReferences()
        {
            int missing = 0;
            MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include);

            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (!(behaviour is ReelController)
                    && !(behaviour is SlotMachineController)
                    && !(behaviour is HudController)
                    && !(behaviour is PaytableModal)
                    && !(behaviour is ConfirmResetModal)
                    && !(behaviour is ProceduralSfxController))
                {
                    continue;
                }

                SerializedObject serialized = new SerializedObject(behaviour);
                SerializedProperty property = serialized.GetIterator();
                while (property.NextVisible(true))
                {
                    if (property.name == "m_Script")
                    {
                        continue;
                    }

                    if (property.propertyType == SerializedPropertyType.ObjectReference
                        && property.objectReferenceValue == null)
                    {
                        Debug.LogError(
                            $"[SlotGame] Missing reference on {behaviour.GetType().Name}.{property.propertyPath} " +
                            $"({behaviour.gameObject.name}). Re-run Build Complete Project.",
                            behaviour);
                        missing++;
                    }
                }
            }

            return missing;
        }

        private static string GetBuildOutputPath()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-buildOutput", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "--output", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(args[i + 1]);
                }
            }

            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Build/WebGL"));
        }
    }
}
#endif
