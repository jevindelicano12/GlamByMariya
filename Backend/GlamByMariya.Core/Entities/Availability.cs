namespace GlamByMariya.Core.Entities;

// Kind is "early" (before opening, needs artist approval) or "regular"; AfterHours = the session ends after closing.
public record AvailableTimeSlot(string Start, string Label, bool Available, string? Reason, string Kind = "regular", bool AfterHours = false);
public record AvailableDate(string Date, string Status, int AvailableCount, List<AvailableTimeSlot> Slots);
public record AvailabilityCalendar(string Today, string Month, int DurationMinutes, int BufferMinutes, List<AvailableDate> Days);
