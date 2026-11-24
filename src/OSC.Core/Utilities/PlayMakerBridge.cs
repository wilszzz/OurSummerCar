using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace OSC.Core.Utilities
{
    public sealed class PM_Hook : FsmStateAction
    {
        private readonly Action action;
        private readonly bool autoFinish;

        public PM_Hook(Action action, bool autoFinish = true)
        {
            this.action = action;
            this.autoFinish = autoFinish;
        }

        public override void OnEnter()
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OSC PM_Hook Error] {ex.Message}");
            }

            if (autoFinish)
            {
                Finish();
            }
        }
    }

    public static class PlayMakerBridge
    {
        public static void InitializeSafe(this PlayMakerFSM fsm)
        {
            if (fsm != null && fsm.Fsm != null && !fsm.Fsm.Initialized)
            {
                fsm.Fsm.InitData();
            }
        }

        public static bool HasState(this PlayMakerFSM fsm, string stateName)
        {
            if (fsm == null || string.IsNullOrEmpty(stateName)) return false;
            fsm.InitializeSafe();
            if (fsm.FsmStates == null) return false;
            for (int i = 0; i < fsm.FsmStates.Length; i++)
            {
                if (fsm.FsmStates[i] != null && fsm.FsmStates[i].Name == stateName)
                {
                    return true;
                }
            }
            return false;
        }

        public static FsmState GetState(this PlayMakerFSM fsm, string stateName)
        {
            if (fsm == null || string.IsNullOrEmpty(stateName)) return null;
            fsm.InitializeSafe();
            if (fsm.FsmStates == null) return null;
            for (int i = 0; i < fsm.FsmStates.Length; i++)
            {
                if (fsm.FsmStates[i] != null && fsm.FsmStates[i].Name == stateName)
                {
                    return fsm.FsmStates[i];
                }
            }
            return null;
        }

        public static FsmEvent AddSafeEvent(this PlayMakerFSM fsm, string eventName)
        {
            if (fsm == null || string.IsNullOrEmpty(eventName)) return null;
            fsm.InitializeSafe();

            FsmEvent fsmEvent = FsmEvent.GetFsmEvent(eventName) ?? new FsmEvent(eventName);
            List<FsmEvent> events = fsm.FsmEvents != null ? fsm.FsmEvents.ToList() : new List<FsmEvent>();
            if (!events.Contains(fsmEvent))
            {
                events.Add(fsmEvent);
                if (fsm.Fsm != null) fsm.Fsm.Events = events.ToArray();
            }
            return fsmEvent;
        }

        public static FsmEvent AddSafeGlobalTransition(this PlayMakerFSM fsm, string eventName, string toStateName)
        {
            if (fsm == null) return null;
            FsmEvent ev = fsm.AddSafeEvent(eventName);
            AddSafeGlobalTransition(fsm, ev, toStateName);
            return ev;
        }

        public static void AddSafeGlobalTransition(this PlayMakerFSM fsm, FsmEvent fsmEvent, string toStateName)
        {
            if (fsm == null || fsmEvent == null || string.IsNullOrEmpty(toStateName) || fsm.Fsm == null) return;
            fsm.InitializeSafe();

            List<FsmTransition> transitions = fsm.Fsm.GlobalTransitions != null
                ? fsm.Fsm.GlobalTransitions.ToList()
                : new List<FsmTransition>();

            for (int i = 0; i < transitions.Count; i++)
            {
                var tr = transitions[i];
                if (tr != null && tr.FsmEvent == fsmEvent && tr.ToState == toStateName)
                {
                    return;
                }
            }

            transitions.Add(new FsmTransition
            {
                FsmEvent = fsmEvent,
                ToState = toStateName
            });
            fsm.Fsm.GlobalTransitions = transitions.ToArray();
            fsm.Fsm.InitData();
        }

        public static void InsertAction(this PlayMakerFSM fsm, string stateName, Action action, int index = 0, bool autoFinish = true)
        {
            FsmState state = fsm.GetState(stateName);
            if (state == null || action == null) return;

            List<FsmStateAction> list = state.Actions != null ? state.Actions.ToList() : new List<FsmStateAction>();
            var hook = new PM_Hook(action, autoFinish);
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

        public static PlayMakerFSM FindFsm(GameObject go, string fsmName)
        {
            if (go == null) return null;
            var fsms = go.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < fsms.Length; i++)
            {
                if (fsms[i] != null && fsms[i].FsmName == fsmName)
                {
                    return fsms[i];
                }
            }
            return null;
        }

        public static Transform FindChildRecursive(Transform root, string targetName)
        {
            if (root == null || string.IsNullOrEmpty(targetName)) return null;
            if (root.name == targetName) return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform res = FindChildRecursive(root.GetChild(i), targetName);
                if (res != null) return res;
            }
            return null;
        }
    }
}
