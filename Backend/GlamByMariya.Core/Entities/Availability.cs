namespace GlamByMariya.Core.Entities;

public record AvailableTimeSlot(string Start, string Label, bool Available, string? Reason);
public record AvailableDate(string Date, string Status, int AvailableCount, List<AvailableTimeSlot> Slots);
public record AvailabilityCalendar(string Today, string Month, int DurationMinutes, int BufferMinutes, List<AvailableDate> Days);
