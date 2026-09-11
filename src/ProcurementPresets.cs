using System;
using System.Collections.Generic;
using System.Linq;

namespace MabinogiBarter
{
    public sealed class ProcurementMethodPreset
    {
        public int Slot { get; set; }
        public Dictionary<string, string> Choices { get; set; }
    }

    public static class ProcurementPresets
    {
        public const int SlotCount = 5;

        public static void Normalize(ProgressState state)
        {
            bool migrate = state.ProcurementMethodPresets == null;
            var existing = state.ProcurementMethodPresets ?? new List<ProcurementMethodPreset>();
            var slots = new List<ProcurementMethodPreset>();
            for (int slot = 1; slot <= SlotCount; slot++)
            {
                var saved = existing.FirstOrDefault(p => p != null && p.Slot == slot);
                slots.Add(new ProcurementMethodPreset { Slot = slot, Choices = CopyChoices(migrate && slot == 1
                    ? state.ProcurementChoices : saved == null ? null : saved.Choices) });
            }
            state.ProcurementMethodPresets = slots;
            if (migrate || state.ActiveProcurementPresetSlot < 1 || state.ActiveProcurementPresetSlot > SlotCount)
                state.ActiveProcurementPresetSlot = 1;
        }

        public static void SaveCurrent(ProgressState state, ProcurementPlanner planner)
        {
            Normalize(state);
            state.ProcurementMethodPresets[state.ActiveProcurementPresetSlot - 1].Choices = Snapshot(state, planner);
        }

        public static List<string> ChangedNames(ProgressState state, ProcurementPlanner planner, int slot)
        {
            ValidateSlot(slot);
            if (slot == state.ActiveProcurementPresetSlot) return new List<string>();
            var saved = state.ProcurementMethodPresets == null ? null : state.ProcurementMethodPresets.FirstOrDefault(p => p != null && p.Slot == slot);
            var target = new ProgressState { ProcurementChoices = CopyChoices(saved == null ? null : saved.Choices) };
            var currentChoices = Snapshot(state, planner); var nextChoices = Snapshot(target, planner);
            return currentChoices.Keys.Concat(nextChoices.Keys).Distinct(StringComparer.Ordinal)
                .Where(name => Get(currentChoices, name) != Get(nextChoices, name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
        }

        public static List<string> Switch(ProgressState state, ProcurementPlanner planner, int slot)
        {
            ValidateSlot(slot); Normalize(state);
            if (slot == state.ActiveProcurementPresetSlot) return new List<string>();
            var changed = ChangedNames(state, planner, slot);
            SaveCurrent(state, planner);
            var target = new ProgressState { ProcurementChoices = state.ProcurementMethodPresets[slot - 1].Choices };
            state.ProcurementChoices = Snapshot(target, planner);
            foreach (string name in changed) ProcurementReadiness.InvalidateChoice(state, name);
            state.ActiveProcurementPresetSlot = slot;
            return changed;
        }

        static Dictionary<string, string> Snapshot(ProgressState state, ProcurementPlanner planner)
        {
            var result = new ProgressState();
            foreach (string name in (state.ProcurementChoices ?? new Dictionary<string, string>()).Keys)
                planner.SetChoice(result, name, planner.GetChoice(state, name));
            return result.ProcurementChoices;
        }

        static Dictionary<string, string> CopyChoices(Dictionary<string, string> choices)
        {
            return (choices ?? new Dictionary<string, string>()).Where(p => !String.IsNullOrWhiteSpace(p.Key) && !String.IsNullOrWhiteSpace(p.Value))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        }

        static string Get(Dictionary<string, string> choices, string name) { string choice; return choices.TryGetValue(name, out choice) ? choice : "buy"; }
        static void ValidateSlot(int slot) { if (slot < 1 || slot > SlotCount) throw new ArgumentOutOfRangeException("slot"); }
    }
}
