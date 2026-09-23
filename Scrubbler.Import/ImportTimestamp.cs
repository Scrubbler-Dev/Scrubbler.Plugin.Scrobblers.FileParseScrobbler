namespace Scrubbler.Import;

public static class ImportTimestamp
{
    public static DateTimeOffset AtTime(DateTimeOffset runTime, int days, TimeSpan time, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(runTime, zone);
        var target = DateTime.SpecifyKind(local.Date.AddDays(-days).Add(time), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(target))
            throw new ArgumentException("The selected scrobble time does not exist because of a daylight-saving change.");
        if (zone.IsAmbiguousTime(target) && zone.GetAmbiguousTimeOffsets(target).Contains(local.Offset))
            return new DateTimeOffset(target, local.Offset).ToUniversalTime();
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(target, zone));
    }

    public static DateTimeOffset Backdate(DateTimeOffset timestamp, int days, TimeZoneInfo zone)
    {
        if (days is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(days));
        if (days == 0) return timestamp;
        var local = TimeZoneInfo.ConvertTime(timestamp, zone);
        var target = DateTime.SpecifyKind(local.DateTime.AddDays(-days), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(target))
            throw new ArgumentException("The backdated time does not exist because of a daylight-saving change.");
        // Preserve the local clock time even when the target date has a different UTC offset.
        if (zone.IsAmbiguousTime(target) && zone.GetAmbiguousTimeOffsets(target).Contains(local.Offset))
            return new DateTimeOffset(target, local.Offset).ToUniversalTime();
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(target, zone));
    }
}
