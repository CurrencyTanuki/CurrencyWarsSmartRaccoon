using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

internal static class Phase2FormationSlotKey
{
    internal static string Format(FormationZone zone, int slotIndex) =>
        $"{zone}:{slotIndex}";

    internal static bool TryParse(
        string? value,
        out FormationZone zone,
        out int slotIndex)
    {
        zone = default;
        slotIndex = -1;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.LastIndexOf(':');
        return separator > 0 &&
               Enum.TryParse(value[..separator], ignoreCase: false, out zone) &&
               zone is FormationZone.Front or FormationZone.Back or FormationZone.Bench &&
               int.TryParse(value[(separator + 1)..], out slotIndex) &&
               slotIndex >= 0;
    }
}

internal static class Phase2FormationRetrySelector
{
    internal static IReadOnlySet<string> SelectRetrySlotKeys(
        Phase2OperationalState? state)
    {
        if (state?.PageFamily != Phase2PageFamily.Preparation)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (state.FormationSlotObservationsAreAtomic &&
            state.FormationSlotObservations.Any(item =>
                item.Occupancy == Phase2FormationSlotOccupancy.Uncertain))
        {
            // A drag/move is one transaction. If any destination is uncertain,
            // retry the whole observed source+destination set so Empty and
            // Recognized are committed together on a later frame.
            return state.FormationSlotObservations
                .Where(item => item.Zone is FormationZone.Front or
                    FormationZone.Back or FormationZone.Bench)
                .Select(item => Phase2FormationSlotKey.Format(
                    item.Zone,
                    item.SlotIndex))
                .ToHashSet(StringComparer.Ordinal);
        }

        var retries = state.FormationSlotObservations
            .Where(item =>
                item.Occupancy == Phase2FormationSlotOccupancy.Uncertain &&
                item.Zone is FormationZone.Front or
                    FormationZone.Back or FormationZone.Bench)
            .Select(item => Phase2FormationSlotKey.Format(
                item.Zone,
                item.SlotIndex))
            .ToHashSet(StringComparer.Ordinal);
        retries.UnionWith((state.Formation.Value ?? [])
            .Where(slot =>
                slot.Zone is FormationZone.Front or FormationZone.Back or FormationZone.Bench &&
                (!slot.CanDriveDecisions ||
                 string.IsNullOrWhiteSpace(slot.CharacterId) ||
                 slot.CharacterId.StartsWith(
                     "unknown-formation-unit",
                     StringComparison.OrdinalIgnoreCase) ||
                 slot.FinalEquipmentSlots.Any(item =>
                     !item.CanDriveDecisions ||
                     item.Occupancy == EquipmentSlotOccupancy.Unknown) ||
                 slot.SpecialEquipment is
                 {
                     Occupancy: EquipmentSlotOccupancy.Unknown
                 }))
            .Select(slot => Phase2FormationSlotKey.Format(slot.Zone, slot.SlotIndex)));
        return retries;
    }

    internal static Phase2IncrementalSelection? BuildSelection(
        Phase2OperationalState? state,
        string? knownPageId,
        int backSlotCount = 6)
    {
        var slots = SelectRetrySlotKeys(state);
        return slots.Count == 0
            ? null
            : new Phase2IncrementalSelection(
                new HashSet<string>(StringComparer.Ordinal)
                {
                    Phase2IncrementalFields.Formation
                },
                knownPageId,
                slots,
                state?.FormationSlotTransactionId,
                state?.FormationSlotObservationsAreAtomic == true,
                Math.Clamp(backSlotCount, 6, 9));
    }
}
