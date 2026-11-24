using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace OurSummerCar.Core
{
    public class PM_Hook : FsmStateAction
    {
        public Action Action;
        public bool EveryFrame;

        public PM_Hook(Action action, bool everyFrame = false)
        {
            Action = action;
            EveryFrame = everyFrame;
        }

        public override void OnEnter()
        {
            if (!EveryFrame)
            {
                try
                {
                    Action?.Invoke();
                }
                catch (Exception ex)
                {
                    ModConsole.Print($"[PM_Hook Error] {ex.Message}");
                }
                Finish();
            }
        }

        public override void OnUpdate()
        {
            if (EveryFrame)
            {
                try
                {
                    Action?.Invoke();
                }
                catch (Exception ex)
                {
                    ModConsole.Print($"[PM_Hook Error] {ex.Message}");
                }
            }
        }
    }

    public static class PlayMakerUtilities
    {
        public static Transform FindChildRecursive(Transform parent, string childName)
        {
            if (parent == null || string.IsNullOrEmpty(childName)) return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == childName) return child;
                var found = FindChildRecursive(child, childName);
                if (found != null) return found;
            }
            return null;
        }

        public static PlayMakerFSM FindFsmOnGameObject(GameObject go, string fsmName)
        {
            if (go == null) return null;
            var fsms = go.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < fsms.Length; i++)
            {
                if (fsms[i] != null && (string.IsNullOrEmpty(fsmName) || fsms[i].FsmName == fsmName))
                {
                    return fsms[i];
                }
            }
            return null;
        }

        public static void AddSafeGlobalTransition(this PlayMakerFSM pm, string eventName, string stateName)
        {
            if (pm == null || string.IsNullOrEmpty(eventName) || string.IsNullOrEmpty(stateName)) return;
            FsmEvent ev = pm.AddSafeEvent(eventName);
            pm.AddSafeGlobalTransition(ev, stateName);
        }

        public static void Initialize(this PlayMakerFSM fsm)
        {
            if (fsm != null && fsm.Fsm != null)
            {
                fsm.Fsm.InitData();
            }
        }

        public static bool HasState(this PlayMakerFSM pm, string stateName)
        {
            if (pm == null || pm.FsmStates == null) return false;
            for (int i = 0; i < pm.FsmStates.Length; i++)
            {
                if (pm.FsmStates[i] != null && pm.FsmStates[i].Name == stateName) return true;
            }
            return false;
        }

        public static FsmEvent AddSafeEvent(this PlayMakerFSM fsm, string eventName)
        {
            if (fsm == null || fsm.Fsm == null) return null;
            fsm.Fsm.InitData();
            FsmEvent fsmEvent = FsmEvent.GetFsmEvent(eventName);
            if (fsmEvent == null)
            {
                fsmEvent = new FsmEvent(eventName);
            }
            List<FsmEvent> list = fsm.FsmEvents != null ? fsm.FsmEvents.ToList() : new List<FsmEvent>();
            if (!list.Contains(fsmEvent))
            {
                list.Add(fsmEvent);
                fsm.Fsm.Events = list.ToArray();
            }
            fsm.Fsm.InitData();
            return fsmEvent;
        }

        public static void AddSafeGlobalTransition(this PlayMakerFSM pm, FsmEvent fsmEvent, string stateName)
        {
            if (pm == null || pm.Fsm == null || fsmEvent == null) return;
            pm.Fsm.InitData();
            List<FsmTransition> list = pm.Fsm.GlobalTransitions != null ? pm.Fsm.GlobalTransitions.ToList() : new List<FsmTransition>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].FsmEvent != null && list[i].FsmEvent.Name == fsmEvent.Name && list[i].ToState == stateName)
                {
                    return; // Исключаем дубликаты глобальных переходов при повторной инициализации
                }
            }
            list.Add(new FsmTransition
            {
                FsmEvent = fsmEvent,
                ToState = stateName
            });
            pm.Fsm.GlobalTransitions = list.ToArray();
            pm.Fsm.InitData();
        }

        public static FsmEvent AddGlobalTransitionByName(this PlayMakerFSM pm, string eventName, string toStateName)
        {
            if (pm == null || pm.Fsm == null) return null;
            FsmEvent fsmEvent = pm.AddEvent(eventName);
            MSCLoader.PlayMakerExtensions.AddGlobalTransition(pm, fsmEvent, toStateName);
            return fsmEvent;
        }

        public static void InsertAction(this PlayMakerFSM pm, string stateName, Action action, int index = -1, bool everyFrame = false)
        {
            if (pm == null) return;
            FsmState state = pm.GetState(stateName);
            if (state == null)
            {
                ModConsole.Print($"[InsertAction] State '{stateName}' is null on FSM '{pm.FsmName}' on object '{pm.gameObject.name}'");
                return;
            }
            var list = (state.Actions != null) ? state.Actions.ToList() : new List<FsmStateAction>();
            var hook = new PM_Hook(action, everyFrame);
            if (index < 0 || index >= list.Count)
            {
                list.Add(hook);
            }
            else
            {
                list.Insert(index, hook);
            }
            state.Actions = list.ToArray();
        }
    }
}
