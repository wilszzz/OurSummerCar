using System;
using System.IO;
using System.Reflection;
using MSCLoader;

namespace OurSummerCar.Bootstrap
{
    /// <summary>
    /// Минимальный загрузчик для MSCLoader: не имеет ни одной статической зависимости на OSC.Core,
    /// поэтому MSCLoader.GetTypes() никогда не падает с ReflectionTypeLoadException.
    /// </summary>
    public sealed class OurSummerCarMod : Mod
    {
        public override string ID => "OurSummerCar";
        public override string Name => "Our Summer Car (Modular)";
        public override string Version => "0.1.0";
        public override string Author => "OurSummerCar Team";
        public override string Description => "Modular co-op multiplayer with isolated assemblies and core event bridges.";

        private static bool resolverRegistered;
        private static string assetRoot;
        private static string coreFolder;
        private static string modulesFolder;

        private Action onMenuLoadDelegate;
        private Action onLoadDelegate;
        private Action onUpdateDelegate;
        private Action onGuiDelegate;

        private static void EnsureResolver()
        {
            if (resolverRegistered) return;
            resolverRegistered = true;

            string modsDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            assetRoot = Path.Combine(modsDir, "Assets\\OurSummerCar");
            coreFolder = Path.Combine(assetRoot, "Core");
            modulesFolder = Path.Combine(assetRoot, "Modules");

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string simpleName = new AssemblyName(args.Name).Name + ".dll";
                string coreCandidate = Path.Combine(coreFolder, simpleName);
                if (File.Exists(coreCandidate)) return Assembly.LoadFrom(coreCandidate);

                string moduleCandidate = Path.Combine(modulesFolder, simpleName);
                if (File.Exists(moduleCandidate)) return Assembly.LoadFrom(moduleCandidate);

                return null;
            };
        }

        public override void ModSetup()
        {
            try
            {
                EnsureResolver();

                string coreDll = Path.Combine(coreFolder, "OSC.Core.dll");
                if (!File.Exists(coreDll))
                {
                    ModConsole.Print($"<color=red>[OurSummerCar]</color> Не найден файл ядра: {coreDll}");
                    return;
                }

                Assembly coreAsm = Assembly.LoadFrom(coreDll);
                Type hostType = coreAsm.GetType("OSC.Core.Runtime.OscRuntimeHost");
                if (hostType == null)
                {
                    ModConsole.Print("<color=red>[OurSummerCar]</color> Тип OSC.Core.Runtime.OscRuntimeHost не найден.");
                    return;
                }

                object host = Activator.CreateInstance(hostType);
                MethodInfo initMethod = hostType.GetMethod("Initialize", new[] { typeof(string) });
                initMethod?.Invoke(host, new object[] { assetRoot });

                onMenuLoadDelegate = (Action)Delegate.CreateDelegate(typeof(Action), host, hostType.GetMethod("OnMenuLoad"));
                onLoadDelegate = (Action)Delegate.CreateDelegate(typeof(Action), host, hostType.GetMethod("OnLoad"));
                onUpdateDelegate = (Action)Delegate.CreateDelegate(typeof(Action), host, hostType.GetMethod("OnUpdate"));
                onGuiDelegate = (Action)Delegate.CreateDelegate(typeof(Action), host, hostType.GetMethod("OnGUI"));

                SetupFunction(Setup.OnMenuLoad, () => onMenuLoadDelegate?.Invoke());
                SetupFunction(Setup.OnLoad, () => onLoadDelegate?.Invoke());
                SetupFunction(Setup.Update, () => onUpdateDelegate?.Invoke());
                SetupFunction(Setup.OnGUI, () => onGuiDelegate?.Invoke());
            }
            catch (Exception ex)
            {
                ModConsole.Print($"<color=red>[OurSummerCar Bootstrap Error]</color> {ex.Message}");
            }
        }
    }
}
