using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using WinPerfOptimizer.Core.Models;

namespace WinPerfOptimizer.Core.Services;

public static class BatteryReportXmlParser
{
    public static BatteryHealthReport Parse(string xmlContent, double? liveChargePercent = null, double? liveRateMw = null)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
            return new BatteryHealthReport(BatteryPresence.ReportUnavailable, [], false);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };

        using var sr = new StringReader(xmlContent);
        using var reader = XmlReader.Create(sr, settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root;
        if (root is null)
            return new BatteryHealthReport(BatteryPresence.ReportUnavailable, [], false);

        var csVal = root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("ConnectedStandby", StringComparison.OrdinalIgnoreCase))?.Value;
        var connectedStandby = string.Equals(csVal, "1", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(csVal, "true", StringComparison.OrdinalIgnoreCase);

        var batteries = new List<BatteryUnitHealth>();
        foreach (var bat in root.Descendants().Where(e => e.Name.LocalName.Equals("Battery", StringComparison.OrdinalIgnoreCase)))
        {
            var id = bat.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Id", StringComparison.OrdinalIgnoreCase))?.Value;
            var mfg = bat.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase))?.Value;
            var chem = bat.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Chemistry", StringComparison.OrdinalIgnoreCase))?.Value;
            var designStr = bat.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("DesignCapacity", StringComparison.OrdinalIgnoreCase))?.Value;
            var fullStr = bat.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("FullChargeCapacity", StringComparison.OrdinalIgnoreCase))?.Value;
            var cycStr = bat.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("CycleCount", StringComparison.OrdinalIgnoreCase))?.Value;

            if (!int.TryParse(designStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var design) || design <= 0)
                continue;
            _ = int.TryParse(fullStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var full);
            int? cycle = int.TryParse(cycStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cyc) && cyc > 0 ? cyc : null;

            var wear = Math.Clamp(Math.Round(100.0 * (1.0 - (double)full / design), 1), 0.0, 100.0);
            var rating = wear switch
            {
                < 15.0 => BatteryWearRating.Healthy,
                < 30.0 => BatteryWearRating.Fair,
                < 50.0 => BatteryWearRating.Degraded,
                _ => BatteryWearRating.ReplaceRecommended
            };

            batteries.Add(new BatteryUnitHealth(
                string.IsNullOrWhiteSpace(id) ? $"BAT{batteries.Count + 1}" : id,
                string.IsNullOrWhiteSpace(mfg) ? "Unknown" : mfg,
                string.IsNullOrWhiteSpace(chem) ? "Li-Ion" : chem,
                design,
                full,
                wear,
                cycle,
                liveChargePercent,
                liveRateMw,
                rating,
                []));
        }

        if (batteries.Count == 0)
            return new BatteryHealthReport(BatteryPresence.NotPresent, [], connectedStandby);

        var history = new List<BatteryCapacityPoint>();
        foreach (var entry in root.Descendants().Where(e => e.Name.LocalName.Equals("HistoryEntry", StringComparison.OrdinalIgnoreCase)))
        {
            var startAttr = entry.Attributes().FirstOrDefault(a =>
                a.Name.LocalName.Equals("LocalDate", StringComparison.OrdinalIgnoreCase)
                || a.Name.LocalName.Equals("PeriodStart", StringComparison.OrdinalIgnoreCase))?.Value;
            var fullAttr = entry.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("FullChargeCapacity", StringComparison.OrdinalIgnoreCase))?.Value;
            var designAttr = entry.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("DesignCapacity", StringComparison.OrdinalIgnoreCase))?.Value;

            if (DateOnly.TryParse(startAttr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                && int.TryParse(fullAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f)
                && int.TryParse(designAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
                && d > 0)
            {
                history.Add(new BatteryCapacityPoint(dt, f, d));
            }
        }

        if (history.Count > 0)
            batteries[0] = batteries[0] with { History = history.TakeLast(12).ToList() };

        return new BatteryHealthReport(BatteryPresence.Present, batteries, connectedStandby);
    }
}
