using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Regions.Contracts;

namespace Regions.Engine
{
    /// <summary>
    /// SHA-256 of the engine's canonical inputs (QuoteProvenance.InputsSha256). The canonical form is a private
    /// token stream, not JSON, so it needs no serializer (V-03). Every list is sorted first, so input order never
    /// changes the hash. Doubles are written as their raw IEEE-754 bits, so the hash does not depend on
    /// number formatting or culture. ExportedAtUtc is excluded: the engine must not read wall-clock fields.
    /// </summary>
    public static class InputHasher
    {
        private const string FormatTag = "regions-quote-inputs-v1";

        public static string Hash(RegionState state, QuoteRequest request)
        {
            var w = new Writer();
            w.Str(FormatTag);
            w.Str(request.DestCityId); w.Str(request.ResourceId);
            w.Int(state.CurrentRegionSeq);
            WriteManifest(w, state.Manifest);

            var defs = state.Resources.OrderBy(r => r.ResourceId, StringComparer.Ordinal).ToList();
            w.Int(defs.Count);
            foreach (var d in defs)
            {
                w.Str(d.ResourceId);
                w.Int(d.AllowedFreightModes.Count);
                foreach (var mode in d.AllowedFreightModes) w.Enum(mode);
            }

            var snaps = state.LatestSnapshots.OrderBy(s => s.Snapshot.CityId, StringComparer.Ordinal).ToList();
            w.Int(snaps.Count);
            foreach (var pub in snaps) { w.Int(pub.RegionSeq); WriteSnapshot(w, pub.Snapshot); }

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(new UTF8Encoding(false).GetBytes(w.ToString()));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static void WriteManifest(Writer w, RegionManifest m)
        {
            w.Int(m.SchemaVersion); w.Str(m.RegionId); w.Int(m.Revision);

            var cities = m.Cities.OrderBy(c => c.CityId, StringComparer.Ordinal).ToList();
            w.Int(cities.Count);
            foreach (var c in cities)
            {
                w.Str(c.CityId); w.Str(c.DisplayName); w.Str(c.SaveRef);
                w.Bool(c.Placement != null);
                if (c.Placement != null)
                {
                    var p = c.Placement;
                    w.Num(p.OriginXMetres); w.Num(p.OriginYMetres); w.Num(p.SizeXMetres); w.Num(p.SizeYMetres);
                    w.Num(p.RotationDegrees); w.Enum(p.MapProfile);
                }
            }

            var links = m.Links.OrderBy(l => l.LinkId, StringComparer.Ordinal).ToList();
            w.Int(links.Count);
            foreach (var l in links)
            {
                w.Str(l.LinkId); w.Str(l.A.CityId); w.Str(l.A.EndpointKey); w.Str(l.B.CityId); w.Str(l.B.EndpointKey);
                w.Enum(l.Mode); w.Num(l.DistanceKm); w.Bool(l.Bidirectional); w.Enum(l.Status);
            }

            var gws = m.WorldGateways.OrderBy(g => g.CityId, StringComparer.Ordinal).ThenBy(g => g.EndpointKey, StringComparer.Ordinal).ToList();
            w.Int(gws.Count);
            foreach (var g in gws) { w.Str(g.CityId); w.Str(g.EndpointKey); w.Enum(g.Mode); w.Num(g.DistanceKm); }

            var costs = m.Parameters.ModeCosts.OrderBy(c => c.Mode).ToList();
            w.Int(costs.Count);
            foreach (var c in costs) { w.Enum(c.Mode); w.Num(c.FixedCost); w.Num(c.CostPerKm); }

            var prices = m.Parameters.ResourcePrices.OrderBy(r => r.ResourceId, StringComparer.Ordinal).ToList();
            w.Int(prices.Count);
            foreach (var r in prices) { w.Str(r.ResourceId); w.Num(r.RegionalProducerPriceIndex); w.Num(r.WorldProducerPriceIndex); }

            w.Int(m.Parameters.Staleness.MaxRegionSeqAge);

            var scarcity = m.Parameters.Scarcity;
            w.Bool(scarcity != null);
            if (scarcity != null) { w.Num(scarcity.PremiumSlope); w.Num(scarcity.MaxPremium); }

            // A missing optional policies block means no policies, so it hashes like an empty one.
            var policies = m.Parameters.Policies ?? new PolicySet();
            var tariffs = policies.ImportTariffs.OrderBy(t => t.PolicyId, StringComparer.Ordinal).ToList();
            w.Int(tariffs.Count);
            foreach (var t in tariffs) { w.Str(t.PolicyId); w.Str(t.ResourceId); w.Num(t.AdValoremPercent); w.Num(t.SpecificPerUnit); }

            var subsidies = policies.FreightSubsidies.OrderBy(s => s.PolicyId, StringComparer.Ordinal).ToList();
            w.Int(subsidies.Count);
            foreach (var s in subsidies)
            {
                w.Str(s.PolicyId); w.Str(s.ResourceId); w.Enum(s.Mode);
                w.Bool(s.LinkIds != null);
                if (s.LinkIds != null)
                {
                    var ids = s.LinkIds.OrderBy(x => x, StringComparer.Ordinal).ToList();
                    w.Int(ids.Count);
                    foreach (var id in ids) w.Str(id);
                }
                w.Num(s.PercentOfFreight); w.Num(s.BudgetPerGameDay);
            }
        }

        private static void WriteSnapshot(Writer w, CitySnapshot s)
        {
            w.Int(s.SchemaVersion); w.Str(s.CityId); w.Int(s.Seq); w.Int(s.ParentSeq);
            w.Str(s.Producer.Tool); w.Str(s.Producer.ToolVersion); w.Str(s.Producer.GameBuild); w.Str(s.Producer.CapabilityReportSha256);
            var pr = s.Profiles;
            w.Enum(pr.Map); w.Enum(pr.Economy); w.Enum(pr.Mobility); w.Enum(pr.Occupancy);
            w.Enum(pr.CompanyControl); w.Enum(pr.FactoryProduction); w.Str(pr.SettingsFingerprint);
            w.Num(s.GameTime.GameDays); w.Long(s.GameTime.RawTick);
            w.Num(s.MeasurementWindowGameHours);

            var res = s.Resources.OrderBy(r => r.ResourceId, StringComparer.Ordinal).ToList();
            w.Int(res.Count);
            foreach (var r in res)
            {
                w.Str(r.ResourceId); w.Num(r.RealizedOutputPerGameDay); w.Num(r.InputUsePerGameDay);
                w.Num(r.CapacityEstimatePerGameDay); w.Int(r.ProducerCompanyCount); w.Enum(r.Confidence); w.Str(r.Source);
            }

            var ocs = s.OutsideConnections.OrderBy(o => o.EndpointKey, StringComparer.Ordinal).ToList();
            w.Int(ocs.Count);
            foreach (var o in ocs)
            {
                w.Str(o.EndpointKey); w.Enum(o.Kind);
                var modes = (o.TransportModes ?? new List<TransportMode>()).OrderBy(x => x).ToList();
                w.Int(modes.Count);
                foreach (var mode in modes) w.Enum(mode);
                w.Bool(o.PositionMetres != null);
                if (o.PositionMetres != null) { w.Num(o.PositionMetres.X); w.Num(o.PositionMetres.Y); w.Num(o.PositionMetres.Z); }
                w.Bool(o.CarriesCargo); w.Bool(o.CarriesPassengers);
            }
        }

        /// <summary>Unambiguous token stream: one token per line, strings length-prefixed.</summary>
        private sealed class Writer
        {
            private readonly StringBuilder _sb = new StringBuilder();

            public void Str(string? s)
            {
                if (s == null) { _sb.Append("n\n"); return; }
                _sb.Append('s').Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s).Append('\n');
            }

            public void Num(double? d)
            {
                if (d == null) { _sb.Append("n\n"); return; }
                _sb.Append('d').Append(BitConverter.DoubleToInt64Bits(d.Value).ToString("x16", CultureInfo.InvariantCulture)).Append('\n');
            }

            public void Int(int? i) => _sb.Append(i == null ? "n" : "i" + i.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            public void Long(long? i) => _sb.Append(i == null ? "n" : "l" + i.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            public void Bool(bool? b) => _sb.Append(b == null ? "n" : b.Value ? "b1" : "b0").Append('\n');
            public void Enum<T>(T e) where T : struct => _sb.Append('e').Append(e.ToString()).Append('\n');

            public override string ToString() => _sb.ToString();
        }
    }
}
