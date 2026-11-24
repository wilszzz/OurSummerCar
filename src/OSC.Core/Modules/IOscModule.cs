using OSC.Core.Modules;

namespace OSC.Core.Modules
{
    /// <summary>
    /// Контракт полностью изолированного модуля игровой механики.
    /// Каждый модуль компилируется в отдельную DLL и взаимодействует с другими только через EventBus/PacketRouter в OSC.Core.
    /// </summary>
    public interface IOscModule
    {
        string ModuleId { get; }
        string DisplayName { get; }
        string StatusSummary { get; }

        void Initialize(OscHostContext context);
        void OnLevelLoaded();
        void OnMenuLoaded();
        void OnUpdate();
        void OnFixedUpdate();
        void OnLateUpdate();
        void OnShutdown();
    }
}
